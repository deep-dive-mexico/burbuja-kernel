import time
import unittest
from monitor import headroom
from admission import recommendation


class HeadroomTests(unittest.TestCase):
    def healthy(self):
        return dict(at=time.time(), cpuPercent=90, pressure=1, diskFreeGiB=10, availableMiB=2000)

    def test_swap_is_not_a_failure_by_itself(self):
        node = self.healthy(); node['swapMiB'] = 12000
        self.assertTrue(headroom(node)['admit'])

    def test_stale_missing_critical_and_protected_service_failure_defer(self):
        for changes in ({'at':0},{'pressure':4},{'diskFreeGiB':1},{'temperature':80},
                        {'throttled':8},{'cpuPercent':99},{'services':[{'name':'dns','ok':False}]}):
            self.assertFalse(headroom(self.healthy() | changes)['admit'])
        self.assertFalse(headroom({})['admit'])
        for value in (None, 'unknown', float('nan'), float('inf'), -1):
            self.assertFalse(headroom(self.healthy() | {'cpuPercent':value})['admit'])

    def test_historical_throttling_does_not_block_current_health(self):
        self.assertTrue(headroom(self.healthy() | {'throttled':0xe0000})['admit'])

    def test_room_admission_requires_sustained_pressure_and_slower_recovery(self):
        state = None
        for i in range(3):
            state = recommendation(self.healthy() | {'cpuPercent':90},state)
            self.assertEqual(state['overloaded'],i==2)
        for i in range(6):
            state = recommendation(self.healthy() | {'cpuPercent':20},state)
            self.assertEqual(state['overloaded'],i<5)
        self.assertTrue(recommendation({'at':0})['overloaded'])


if __name__ == '__main__': unittest.main()
