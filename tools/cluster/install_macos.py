#!/usr/bin/env python3
"""Install this explicitly configured native worker and its health monitor; unrelated services stay independent."""
import argparse
import os
from pathlib import Path
import plistlib
import subprocess
import sys


def install(label, arguments, directory):
    target = Path.home()/'Library/LaunchAgents'/f'{label}.plist'
    domain = f'gui/{os.getuid()}'
    definition = {'Label':label,'ProgramArguments':arguments,'WorkingDirectory':str(directory),
                  'RunAtLoad':True,'KeepAlive':True,'ThrottleInterval':30,
                  'StandardOutPath':str(directory/(label+'.log')),
                  'StandardErrorPath':str(directory/(label+'.err')),
                  'EnvironmentVariables':{'PATH':'/opt/homebrew/bin:/usr/bin:/bin:/usr/sbin:/sbin'}}
    if target.exists():
        prior = plistlib.loads(target.read_bytes())
        if prior.get('ProgramArguments') != arguments:
            raise RuntimeError('Existing LaunchAgent has different ownership: '+label)
        if subprocess.run(['launchctl','print',domain+'/'+label],capture_output=True).returncode == 0:
            return
    target.parent.mkdir(parents=True,exist_ok=True)
    target.write_bytes(plistlib.dumps(definition));target.chmod(0o600)
    subprocess.run(['launchctl','bootstrap',domain,str(target)],check=True)


if __name__ == '__main__':
    ap=argparse.ArgumentParser(description=__doc__)
    ap.add_argument('--root',type=Path,required=True)
    ap.add_argument('--peer',default='pi')
    args=ap.parse_args();root=args.root.resolve()
    install('dev.burbuja.cluster-monitor',[sys.executable,str(root/'monitor.py'),'--root',str(root),'--peer',args.peer],root)
    install('dev.burbuja.compute-worker',[str(root/'worker/Burbuja.Worker')],root/'worker')
    install('dev.burbuja.compute-tunnel',['/usr/bin/ssh','-NT','-o','BatchMode=yes','-o','ExitOnForwardFailure=yes',
        '-o','ServerAliveInterval=15','-o','ServerAliveCountMax=3','-o','ControlMaster=no','-o','ControlPath=none',
        '-R','127.0.0.1:18510:127.0.0.1:18510',args.peer],root)
    print('Installed worker, monitor and reverse loopback tunnel.')
