#!/usr/bin/env python3
"""Private cluster headroom publisher. Remote nodes are SSH leaves; only the Mac initiates SSH."""
import argparse
from datetime import datetime, timezone
import json
import math
from pathlib import Path
import subprocess
import time
from concurrent.futures import ThreadPoolExecutor
from sample import atomic, sample, http
from admission import recommendation


def headroom(node):
    required = ('cpuPercent', 'pressure', 'diskFreeGiB', 'availableMiB')
    reason = 'available'
    if (any(type(node.get(k)) not in (int,float) or not math.isfinite(node[k]) or node[k] < 0 for k in required)
            or not -5 <= time.time()-node.get('at',0) <= 60):
        reason = 'host telemetry unavailable'
    elif node['pressure'] & 4: reason = 'critical memory pressure'
    elif node['diskFreeGiB'] < 5: reason = 'disk reserve'
    elif node['cpuPercent'] > 98: reason = 'CPU saturated'
    elif node.get('temperature',0) >= 78: reason = 'thermal reserve'
    elif node.get('throttled',0) & 0xf: reason = 'thermal or power throttling'
    elif any(not service['ok'] for service in node.get('services', [])): reason = 'protected service unhealthy'
    elif any(c.get('state') == 'running' and 'unhealthy' in c.get('status','') for c in node.get('containers',[])):
        reason = 'container unhealthy'
    return {'observedAt': datetime.fromtimestamp(node.get('at',0), timezone.utc).isoformat(),
            'admit': reason == 'available', 'reason': reason}


def collect(peer):
    start = time.monotonic()
    try:
        # peer and sampler are operator-owned config, never supplied by a workload.
        result = subprocess.run(['ssh','-o','BatchMode=yes','-o','ConnectTimeout=8',peer,
                                 'python3 Developer/burbuja-cluster/sample.py'],
                                capture_output=True,text=True,timeout=25,check=True)
        node = json.loads(result.stdout)
        node['sshMs'] = round((time.monotonic()-start)*1000)
        return node
    except Exception:
        return {'name':peer, 'at':0, 'errors':['SSH observation unavailable'], 'services':[], 'containers':[]}


def tick(root, peer):
    with ThreadPoolExecutor(max_workers=2) as pool:
        future = pool.submit(collect, peer)
        local = sample()
        remote = future.result()
    policy = headroom(local)
    atomic(root/'headroom.json', policy)
    worker = None
    token_file = root/'worker.token'
    if token_file.exists():
        worker = http('http://127.0.0.1:18510/workloads/v1/status', token_file.read_text().strip()).get('body')
    try: previous = json.loads((root/'state.json').read_text()).get('admission',{})
    except (OSError,ValueError): previous = {}
    admission = {n['name']:recommendation(n,previous.get(n['name'])) for n in (local,remote)}
    fleet = local.get('fleet',{})
    realtime_busy = any(s.get('nodeId') == fleet.get('nodeId') and s.get('lobbies',0) > 0 for s in fleet.get('value',[]))
    state = {'at':time.time(), 'nodes':[local,remote], 'worker':worker, 'headroom':policy, 'realtimeBusy':realtime_busy,
             'admissionMode':'observe', 'admission':admission}
    atomic(root/'state.json', state)
    # A cooperative admission budget. Existing jobs finish; nothing gets killed.
    atomic(root/'background.json', {'at':time.time(), 'maxWorkers':0 if realtime_busy or worker and worker.get('active') or not policy['admit'] else 6,
                                   'reason':'transcription active' if worker and worker.get('active') else policy['reason']})
    return state


if __name__ == '__main__':
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument('--root', type=Path, default=Path.home()/'Developer/burbuja-cluster')
    ap.add_argument('--peer', default='pi')
    ap.add_argument('--once', action='store_true')
    args = ap.parse_args()
    while True:
        try: tick(args.root, args.peer)
        except Exception as error: print(type(error).__name__, flush=True)
        if args.once: break
        time.sleep(20)
