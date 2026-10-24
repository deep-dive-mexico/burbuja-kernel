#!/usr/bin/env python3
"""Fast, source-bound worker/cluster development proof. Not a kernel promotion gate."""
import json
from pathlib import Path
import subprocess
import sys
import time
import uuid
import xml.etree.ElementTree as ET

ROOT=Path(__file__).resolve().parents[1]


def main():
    started=time.monotonic()
    directory=ROOT/'artifacts/workload-validation'/uuid.uuid4().hex[:12]
    directory.mkdir(parents=True)
    commit=subprocess.check_output(['git','rev-parse','HEAD'],cwd=ROOT,text=True).strip()
    dirty=bool(subprocess.check_output(['git','status','--porcelain'],cwd=ROOT,text=True).strip())
    receipt={'kind':'focused-workload-development-validation','kernelCommit':commit,'dirty':dirty,
             'promotionGate':False,'status':'incomplete','checks':{}}
    output=directory/'receipt.json'
    output.write_text(json.dumps(receipt,indent=2))
    try:
        result=subprocess.run(['dotnet','test','tests/Burbuja.Worker.Tests','-c','Release','-m:1',
            '--logger','trx;LogFileName=worker.trx','--results-directory',str(directory)],cwd=ROOT)
        counts=ET.parse(directory/'worker.trx').find('.//{*}Counters')
        values={key:int(counts.get(key,'0')) for key in ('total','passed','failed','notExecuted','aborted')}
        receipt['checks']['worker']=values
        if result.returncode or values['total']<1 or values['passed']!=values['total'] or any(values[k] for k in ('failed','notExecuted','aborted')):
            raise RuntimeError('Worker tests did not fully pass')
        sys.path.insert(0,str(ROOT/'tools/cluster'))
        import unittest
        suite=unittest.defaultTestLoader.discover(str(ROOT/'tools/cluster'),pattern='test_*.py')
        tests=unittest.TextTestRunner(verbosity=1).run(suite)
        receipt['checks']['cluster']={'total':tests.testsRun,'failed':len(tests.failures)+len(tests.errors),'skipped':len(tests.skipped)}
        if not tests.testsRun or not tests.wasSuccessful() or tests.skipped:
            raise RuntimeError('Cluster tests did not fully pass')
        receipt['status']='passed'
    except Exception as error:
        receipt.update(status='failed',error=str(error))
    receipt['seconds']=round(time.monotonic()-started,3)
    output.write_text(json.dumps(receipt,indent=2)+'\n')
    print(json.dumps({'status':receipt['status'],'seconds':receipt['seconds'],'receipt':str(output)},indent=2))
    return 0 if receipt['status']=='passed' else 1


if __name__=='__main__': raise SystemExit(main())
