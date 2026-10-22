import ctypes
import unittest
from vision_agent.replica_lab import CandidateScanner, OwnedLab


class OwnedLabTests(unittest.TestCase):
    def setUp(self):
        self.lab = OwnedLab()

    def test_snapshot_contains_actual_owned_buffer_addresses(self):
        snapshot = self.lab.snapshot()
        self.assertEqual(len(snapshot["cells"]), 256)
        cell = next(c for c in snapshot["cells"] if c["address"] == self.lab.currency_address)
        self.assertEqual(cell["value"], 50)
        self.assertEqual(ctypes.c_int32.from_address(cell["address"]).value, 50)
        snapshot["cells"][0]["value"] = -999
        self.assertNotEqual(self.lab.snapshot()["cells"][0]["value"], -999)

    def test_documented_actions(self):
        self.assertEqual(self.lab.perform("collect")["currency"], 75)
        self.assertEqual(self.lab.perform("spend")["currency"], 65)
        self.assertEqual(self.lab.perform("fire")["ammo"], 11)
        self.assertEqual(self.lab.perform("reload")["ammo"], 16)
        before = self.lab.state()
        with self.assertRaises(ValueError):
            self.lab.perform("shell")
        self.assertEqual(before, self.lab.state())

    def test_stale_perform_is_atomic_and_nonmutating(self):
        revision = self.lab.revision
        self.lab.perform("collect", revision)
        before = self.lab.state()
        with self.assertRaises(ValueError):
            self.lab.perform("collect", revision)
        with self.assertRaises(ValueError):
            self.lab.perform("collect", True)
        self.assertEqual(self.lab.state(), before)

    def test_sensitive_write_has_all_boundaries(self):
        address = self.lab.currency_address
        with self.assertRaises(PermissionError):
            self.lab.write(address, 9999, 0)
        self.lab.perform("collect")
        with self.assertRaises(ValueError):
            self.lab.write(address, 9999, 0, True)
        for bad in (-1, 1_000_001, True, "9999"):
            with self.subTest(bad=bad), self.assertRaises(ValueError):
                self.lab.write(address, bad, self.lab.revision, True)
        for bad in (address + 1, 1, self.lab.snapshot()["cells"][0]["address"], True):
            with self.subTest(address=bad), self.assertRaises(ValueError):
                self.lab.write(bad, 1, self.lab.revision, True)
        self.assertEqual(self.lab.write(address, 9999, self.lab.revision, True)["currency"], 9999)

    def test_stop_invalidates_even_approved_write(self):
        revision = self.lab.revision
        self.lab.stop()
        with self.assertRaises(ValueError):
            self.lab.perform("collect")
        with self.assertRaises(ValueError):
            self.lab.write(self.lab.currency_address, 9999, revision, True)
        self.lab.resume()
        with self.assertRaises(ValueError):
            self.lab.write(self.lab.currency_address, 9999, revision, True)
        self.assertEqual(self.lab.state()["currency"], 50)

    def test_resource_limits_fail_without_mutation(self):
        self.lab.write(self.lab.ammo_address, 0, self.lab.revision, True)
        before = self.lab.state()
        with self.assertRaises(ValueError):
            self.lab.perform("fire")
        self.assertEqual(self.lab.state(), before)
        self.lab.write(self.lab.currency_address, 1_000_000, self.lab.revision, True)
        before = self.lab.state()
        with self.assertRaises(ValueError):
            self.lab.perform("collect")
        self.assertEqual(self.lab.state(), before)

    def test_reset_invalidates_snapshot(self):
        revision = self.lab.revision
        self.lab.reset()
        with self.assertRaises(ValueError):
            self.lab.write(self.lab.currency_address, 500, revision, True)


class CandidateScannerTests(unittest.TestCase):
    def test_currency_search_needs_two_changed_observations(self):
        lab, scanner = OwnedLab(), CandidateScanner()
        self.assertEqual(len(scanner.first_scan(lab.snapshot(), 50)), 3)
        with self.assertRaises(ValueError):
            scanner.unique_address()
        lab.perform("collect")
        self.assertEqual(len(scanner.rescan(lab.snapshot(), 75)), 2)
        lab.perform("collect")
        self.assertEqual(scanner.rescan(lab.snapshot(), 100), [lab.currency_address])
        self.assertEqual(scanner.unique_address(), lab.currency_address)
        result = lab.write(scanner.unique_address(), 9999, scanner.revision, True)
        self.assertEqual(result["currency"], 9999)
        self.assertEqual(ctypes.c_int32.from_address(scanner.unique_address()).value, 9999)

    def test_ammo_search_also_independent(self):
        lab, scanner = OwnedLab(), CandidateScanner()
        self.assertEqual(len(scanner.first_scan(lab.snapshot(), 12)), 3)
        lab.perform("fire")
        self.assertEqual(len(scanner.rescan(lab.snapshot(), 11)), 2)
        lab.perform("fire")
        self.assertEqual(scanner.rescan(lab.snapshot(), 10), [lab.ammo_address])

    def test_same_or_stale_observation_rejected(self):
        lab, scanner = OwnedLab(), CandidateScanner()
        scanner.first_scan(lab.snapshot(), 50)
        with self.assertRaises(ValueError):
            scanner.rescan(lab.snapshot(), 75)
        lab.perform("fire")
        with self.assertRaises(ValueError):
            scanner.rescan(lab.snapshot(), 50)
        self.assertEqual(len(scanner.candidates), 3)

    def test_different_target_or_buffer_rejected(self):
        lab, scanner = OwnedLab(), CandidateScanner()
        scanner.first_scan(lab.snapshot(), 50)
        other = OwnedLab()
        other.perform("collect")
        with self.assertRaises(ValueError):
            scanner.rescan(other.snapshot(), 75)
        lab.perform("collect")
        tampered = lab.snapshot()
        tampered["cells"].pop()
        with self.assertRaises(ValueError):
            scanner.rescan(tampered, 75)

    def test_unreadable_value_can_yield_no_candidates_but_never_write(self):
        lab, scanner = OwnedLab(), CandidateScanner()
        self.assertEqual(scanner.first_scan(lab.snapshot(), 999), [])
        with self.assertRaises(ValueError):
            scanner.unique_address()

    def test_malformed_snapshot_rejected(self):
        lab, scanner = OwnedLab(), CandidateScanner()
        snapshot = lab.snapshot()
        snapshot["cells"].append(dict(snapshot["cells"][0]))
        with self.assertRaises(ValueError):
            scanner.first_scan(snapshot, 50)
        with self.assertRaises(ValueError):
            scanner.first_scan(lab.snapshot(), True)
