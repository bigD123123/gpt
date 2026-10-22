"""Full workflow tests with the actual owned ctypes lab, without a display/model."""
import unittest
from vision_agent.replica_lab import OwnedLab
from vision_agent.replica_ai import ReplicaDecision
from vision_agent.replica_flow import ReplicaFlow


class LocalClient:
    def __init__(self):
        self.lab = OwnedLab()

    def request(self, cmd, **fields):
        if cmd == "state":
            return self.lab.state()
        if cmd == "snapshot":
            return self.lab.snapshot()
        return getattr(self.lab, cmd)(**fields)


class FlowTests(unittest.TestCase):
    def setUp(self):
        self.client = LocalClient()
        self.flow = ReplicaFlow(self.client, 999)

    def decision(self, action="observe", stat="currency", value=None):
        if value is None:
            value = self.client.lab.state()[stat]
        return ReplicaDecision(stat, value, action, "Test af kontrolleret beslutning")

    def narrow(self, stat="currency"):
        self.flow.observe()
        counts = [self.flow.accept(self.decision(stat=stat))["count"]]
        for _ in range(2):
            self.flow.observe()
            self.flow.accept(self.decision("collect" if stat == "currency" else "fire", stat))
            self.flow.observe()
            counts.append(self.flow.accept(self.decision(stat=stat))["count"])
        return counts

    def test_full_flow_both_stats_and_final_image_observation(self):
        for stat in ("currency", "ammo"):
            with self.subTest(stat=stat):
                self.setUp()
                self.assertEqual(self.narrow(stat), [3, 2, 1])
                proposal = self.flow.approval()
                self.assertEqual(proposal["value"], 999)
                with self.assertRaises(PermissionError):
                    self.flow.write()
                self.flow.write(approved=True)
                self.assertEqual(self.client.lab.state()[stat], 999)
                self.assertEqual(self.flow.phase, "final")
                self.flow.observe()
                self.assertEqual(self.flow.accept(self.decision(stat=stat))["event"], "visual_verification_passed")
                self.assertEqual(self.flow.phase, "done")

    def test_bad_visual_reading_never_starts_scan(self):
        self.flow.observe()
        with self.assertRaises(ValueError):
            self.flow.accept(self.decision(value=51))
        self.assertEqual(self.flow.scanner.candidates, set())
        self.assertEqual(self.client.lab.state()["currency"], 50)

    def test_human_change_after_screenshot_blocks_decision(self):
        self.flow.observe()
        old = self.decision()
        self.client.lab.perform("collect")
        with self.assertRaisesRegex(ValueError, "forældet"):
            self.flow.accept(old)

    def test_human_change_between_agent_steps_blocks_capture(self):
        self.flow.observe()
        self.flow.accept(self.decision())
        self.client.lab.perform("fire")
        with self.assertRaises(ValueError):
            self.flow.observe()

    def test_atomic_perform_rejects_revision_race(self):
        self.flow.observe()
        self.flow.accept(self.decision())
        self.flow.observe()
        original = self.client.request
        def racing_request(cmd, **fields):
            if cmd == "perform":
                self.client.lab.perform("fire")
            return original(cmd, **fields)
        self.client.request = racing_request
        with self.assertRaises(ValueError):
            self.flow.accept(self.decision("collect"))
        self.assertEqual(self.client.lab.state()["currency"], 50)

    def test_manual_change_invalidates_approval(self):
        self.narrow()
        self.client.lab.perform("collect")
        with self.assertRaises(ValueError):
            self.flow.write(approved=True)
        self.assertNotEqual(self.client.lab.state()["currency"], 999)

    def test_stop_invalidates_approval_and_observation(self):
        self.narrow()
        self.flow.stop()
        with self.assertRaises(ValueError):
            self.flow.write(approved=True)
        with self.assertRaises(ValueError):
            self.flow.observe()
        self.assertEqual(self.client.lab.state()["currency"], 100)

    def test_approval_is_not_reusable(self):
        self.narrow()
        self.flow.write(approved=True)
        with self.assertRaises(ValueError):
            self.flow.write(approved=True)

    def test_change_cannot_switch_selected_stat_or_inject_action(self):
        self.flow.observe()
        self.flow.accept(self.decision())
        self.flow.observe()
        for d in (self.decision("fire", "ammo"), self.decision("shell"), self.decision("finish")):
            with self.subTest(decision=d), self.assertRaises(ValueError):
                self.flow.accept(d)
        self.assertEqual(self.client.lab.state()["currency"], 50)

    def test_final_wrong_image_is_not_success(self):
        self.narrow()
        self.flow.write(approved=True)
        self.flow.observe()
        with self.assertRaises(ValueError):
            self.flow.accept(self.decision(value=100))
        self.assertEqual(self.flow.phase, "final")

    def test_cycle_limit_fails_without_write(self):
        self.flow.max_cycles = 1
        self.flow.observe()
        self.flow.accept(self.decision())
        self.flow.observe()
        self.flow.accept(self.decision("collect"))
        self.flow.observe()
        self.flow.accept(self.decision())
        self.flow.observe()
        with self.assertRaises(ValueError):
            self.flow.accept(self.decision("collect"))
        self.assertEqual(self.client.lab.state()["currency"], 75)
        with self.assertRaises(ValueError):
            self.flow.write(approved=True)

    def test_session_replacement_is_blocked(self):
        self.flow.observe()
        self.flow.accept(self.decision())
        self.client.lab = OwnedLab()
        with self.assertRaises(ValueError):
            self.flow.observe()

    def test_preferred_stat_is_locked_from_first_observation(self):
        flow = ReplicaFlow(self.client, 999, preferred_stat="ammo")
        flow.observe()
        with self.assertRaises(ValueError):
            flow.accept(self.decision(stat="currency"))
        event = flow.accept(self.decision(stat="ammo"))
        self.assertEqual(event["count"], 3)
        self.assertEqual(flow.stat_type, "ammo")

    def test_ammo_preference_rejects_impossible_target_early(self):
        with self.assertRaisesRegex(ValueError, "højst være 1000"):
            ReplicaFlow(self.client, 1001, preferred_stat="ammo")

    def test_ten_consecutive_complete_currency_runs(self):
        for run_number in range(10):
            with self.subTest(run=run_number + 1):
                self.setUp()
                self.assertEqual(self.narrow("currency"), [3, 2, 1])
                self.flow.write(approved=True)
                self.flow.observe()
                result = self.flow.accept(self.decision(stat="currency"))
                self.assertEqual(result["event"], "visual_verification_passed")
                self.assertEqual(self.client.lab.state()["currency"], 999)


if __name__ == "__main__":
    unittest.main()
