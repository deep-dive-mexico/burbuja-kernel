#!/usr/bin/env python3
"""Small read-only host observation. No environment, process arguments or health logs leave the host."""
import json
import os
from pathlib import Path
import platform
import re
import shutil
import subprocess
import time
import urllib.request
import urllib.error


def command(args, timeout=8):
    result = subprocess.run(args, capture_output=True, text=True, timeout=timeout,
                            env={**os.environ, 'TAILSCALE_BE_CLI':'1'})
    if result.returncode:
        raise RuntimeError(f'{Path(args[0]).name}: exit {result.returncode}')
    return result.stdout


def atomic(path, value):
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    temp = path.with_suffix('.tmp')
    temp.write_text(json.dumps(value))
    temp.chmod(0o600)
    temp.replace(path)


def http(url, token=None):
    start = time.monotonic()
    try:
        request = urllib.request.Request(url, headers={'Authorization': 'Bearer '+token} if token else {})
        with urllib.request.urlopen(request, timeout=4) as response:
            body = response.read(1024*1024)
            return {'ok': response.status == 200, 'status':response.status, 'ms': round((time.monotonic()-start)*1000),
                    'body': json.loads(body) if token else None}
    except urllib.error.HTTPError as error:
        return {'ok':False,'status':error.code,'ms':round((time.monotonic()-start)*1000)}
    except Exception:
        return {'ok': False, 'ms': round((time.monotonic()-start)*1000)}


def sample():
    mac = platform.system() == 'Darwin'
    result = {'at': time.time(), 'name': platform.node(), 'cores': os.cpu_count(), 'load': os.getloadavg(),
              'errors': [], 'services': [], 'containers': []}
    disk = shutil.disk_usage(Path.home())
    result.update(diskPercent=round(disk.used/disk.total*100, 1), diskFreeGiB=round(disk.free/2**30, 1))
    try:
        if mac:
            top = command(['top', '-l', '2', '-s', '1', '-n', '0'])
            result['cpuPercent'] = round(100-float(re.findall(r'([\d.]+)% idle', top)[-1]), 1)
            result['pressure'] = int(command(['sysctl', '-n', 'kern.memorystatus_vm_pressure_level']))
            total = int(command(['sysctl', '-n', 'hw.memsize']))
            vm = command(['vm_stat'])
            page = int(re.search(r'page size of (\d+)', vm)[1])
            values = dict((k, int(v)) for k, v in re.findall(r'^([^:]+):\s+(\d+)', vm, re.M))
            available = sum(values.get(k, 0) for k in ('Pages free', 'Pages inactive', 'Pages speculative'))*page
            result['memoryPercent'] = round((1-available/total)*100, 1)
            result['availableMiB'] = round(available/2**20)
            result['swapMiB'] = float(re.search(r'used = ([\d.]+)M', command(['sysctl', '-n', 'vm.swapusage']))[1])
            result['thermal'] = command(['pmset', '-g', 'therm']).strip()
        else:
            def ticks(): return list(map(int, Path('/proc/stat').read_text().splitlines()[0].split()[1:9]))
            before = ticks(); time.sleep(.25); delta = [b-a for a,b in zip(before,ticks())]
            result['cpuPercent'] = round(100*(1-(delta[3]+delta[4])/max(1,sum(delta))), 1)
            memory = dict((k,int(v)) for k,v in re.findall(r'^(\w+):\s+(\d+)', Path('/proc/meminfo').read_text(), re.M))
            result.update(memoryPercent=round(100*(1-memory['MemAvailable']/memory['MemTotal']),1),
                          availableMiB=round(memory['MemAvailable']/1024),
                          swapMiB=round((memory['SwapTotal']-memory['SwapFree'])/1024), pressure=1)
            result['temperature'] = float(Path('/sys/class/thermal/thermal_zone0/temp').read_text())/1000
            result['throttled'] = int(command(['vcgencmd','get_throttled']).strip().split('=')[1],16)
    except Exception as error: result['errors'].append('host metrics: '+str(error)[:80])
    docker = '/Applications/Docker.app/Contents/Resources/bin/docker' if mac else 'docker'
    try:
        rows = command([docker, 'ps', '-a', '--format', '{{json .}}'])
        for row in rows.splitlines():
            d = json.loads(row)
            result['containers'].append({'name': d['Names'], 'state': d['State'], 'status': d['Status']})
    except Exception as error: result['errors'].append('containers: '+str(error)[:80])
    ts = '/Applications/Tailscale.app/Contents/MacOS/Tailscale' if mac else 'tailscale'
    try:
        d = json.loads(command([ts,'status','--json']))
        result['tailscale'] = {'state': d.get('BackendState'), 'health': d.get('Health') or [],
                              'exitNode': bool(d.get('ExitNodeStatus')),
                              'peers': [{'name': p.get('HostName'), 'online': p.get('Online'),
                                         'path': 'direct' if p.get('CurAddr') else 'relay '+p.get('Relay','')}
                                        for p in d.get('Peer',{}).values()
                                        if p.get('HostName','').lower() in ('divemac','raspberrypi','macintosh')]}
    except Exception as error: result['errors'].append('tailscale: '+str(error)[:80])
    config_file = Path.home()/'Developer/burbuja-cluster/node.json'
    config = json.loads(config_file.read_text()) if config_file.exists() else {}
    for name,check in config.get('http', {}).items():
        url = check if isinstance(check,str) else check['url']
        if '{tailnet}' in url:
            try: url = url.replace('{tailnet}', command([ts,'ip','-4']).strip().splitlines()[0])
            except Exception:
                result['services'].append({'name':name,'ok':False}); continue
        observation = http(url)
        expected = 200 if isinstance(check,str) else check.get('status',200)
        observation['ok'] = observation.get('status') == expected
        result['services'].append({'name': name, **{k:v for k,v in observation.items() if k != 'body'}})
    if config.get('dns'):
        dns = config['dns']
        try:
            answer = command(['dig','@'+dns['server'],dns['name'],'+tries=1','+time=2','+stats'])
            result['services'].append({'name':'DNS','ok':'status: NOERROR' in answer,
                                       'ms':int(re.search(r'Query time: (\d+)',answer)[1])})
        except Exception: result['services'].append({'name':'DNS','ok':False})
    for unit in config.get('units', []):
        try:
            args = ['systemctl']+(['--user'] if unit.get('user') else [])+['is-active',unit['name']]
            ok = command(args).strip() == 'active'
        except Exception: ok = False
        result['services'].append({'name':unit['name'],'ok':ok})
    fleet = config.get('fleet')
    if fleet:
        try:
            env = Path(fleet['envFile']).expanduser().read_text().splitlines()
            key = fleet['tokenEnvKey'] + '='
            token = next(line.split('=',1)[1].strip().strip('\"\'') for line in env if line.startswith(key))
            response = http(fleet['url'], token)
            if not response['ok']: raise RuntimeError('fleet unavailable')
            result['fleet'] = response['body']
        except Exception: result['errors'].append('fleet unavailable')
    return result


if __name__ == '__main__':
    print(json.dumps(sample()))
