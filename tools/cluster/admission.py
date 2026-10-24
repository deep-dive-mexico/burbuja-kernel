"""Load-based lobby recommendations, with hysteresis. Observation never changes operator drains."""
import time


def recommendation(node, previous=None, now=None):
    now = time.time() if now is None else now
    previous = previous or {'overloaded':False,'bad':0,'good':0}
    fresh = -5 <= now-node.get('at',0) <= 90
    if not fresh:
        return {'overloaded':True,'bad':0,'good':0,'reason':'host observation stale','action':'defer new rooms'}
    reason = None
    if node.get('temperature',0) >= 78 or node.get('throttled',0) & 0xf: reason = 'thermal or power pressure'
    elif node.get('pressure',1) & 4 or node.get('availableMiB',2048) < 768: reason = 'memory reserve'
    elif node.get('cpuPercent',100) >= 85: reason = 'CPU reserve'
    elif any(not s['ok'] for s in node.get('services',[])): reason = 'protected service unhealthy'
    bad = previous.get('bad',0)+1 if reason else 0
    good = previous.get('good',0)+1 if not reason else 0
    overloaded = previous.get('overloaded',False)
    if bad >= 3: overloaded = True
    if good >= 6: overloaded = False
    return {'overloaded':overloaded,'bad':bad,'good':good,'reason':reason or ('recovering' if overloaded else 'healthy'),
            'action':'defer new rooms' if overloaded else 'accept within measured capacity'}
