"""Client stop gating with a fake pipe process; no desktop or subprocess needed."""
import json
import queue
import threading
import time
import unittest
from unittest.mock import patch

from vision_agent.replica_client import LabClient


class OutputPipe:
    def __init__(self):
        self.lines = queue.Queue()
        self.closed = False

    def readline(self, size=-1):
        return self.lines.get(timeout=2)

    def put(self, obj):
        self.lines.put(json.dumps(obj) + "\n")

    def close(self):
        self.closed = True
        self.lines.put("")


class InputPipe:
    def __init__(self, process):
        self.process = process
        self.closed = False

    def write(self, text):
        request = json.loads(text)
        self.process.sent.append(request)
        self.process.sent_event.set()
        if self.process.delay_command == request["cmd"]:
            self.process.delayed = request
        else:
            self.process.reply(request)
        return len(text)

    def flush(self):
        pass

    def close(self):
        self.closed = True


class FakeProcess:
    def __init__(self):
        self.stdout = OutputPipe()
        self.stdin = InputPipe(self)
        self.sent = []
        self.sent_event = threading.Event()
        self.delay_command = None
        self.delayed = None
        self.returncode = None
        self.revision = 0
        self.stdout.put({"event": "ready", "state": {"currency": 50, "ammo": 12}})

    def reply(self, request):
        self.revision += 1
        self.stdout.put({"id": request["id"], "ok": True, "result": {"revision": self.revision}})
        if request["cmd"] == "close":
            self.returncode = 0
            self.stdout.lines.put("")

    def poll(self):
        return self.returncode

    def wait(self, timeout=None):
        self.returncode = 0
        self.stdout.lines.put("")
        return 0

    def terminate(self):
        self.returncode = -15
        self.stdout.lines.put("")

    def kill(self):
        self.returncode = -9
        self.stdout.lines.put("")


class ClientGateTests(unittest.TestCase):
    def setUp(self):
        self.process = FakeProcess()
        self.popen = patch("vision_agent.replica_client.subprocess.Popen", return_value=self.process)
        self.popen.start()
        self.client = LabClient(timeout=1)

    def tearDown(self):
        self.client.close()
        self.popen.stop()

    def test_halt_blocks_perform_and_write_before_pipe_send(self):
        self.client.halt()
        before = len(self.process.sent)
        for command, fields in (("perform", {"action": "collect"}),
                                ("write", {"address": 123, "value": 999, "approved": True})):
            with self.subTest(command=command), self.assertRaises(RuntimeError):
                self.client.request(command, **fields)
        self.assertEqual(len(self.process.sent), before)
        self.assertFalse(self.client.failed)

    def test_successful_resume_reopens_gate_then_stop_closes_it(self):
        self.client.halt()
        self.client.request("resume")
        self.assertFalse(self.client.halted.is_set())
        self.client.request("perform", action="collect")
        self.client.request("stop")
        self.assertTrue(self.client.halted.is_set())
        with self.assertRaises(RuntimeError):
            self.client.request("write", address=123, value=999, approved=True)
        self.assertEqual([r["cmd"] for r in self.process.sent], ["resume", "perform", "stop"])

    def test_stop_during_resume_cannot_be_cleared_by_late_response(self):
        self.client.halt()
        self.process.delay_command = "resume"
        errors = []
        def resume():
            try:
                self.client.request("resume")
            except Exception as exc:
                errors.append(exc)
        worker = threading.Thread(target=resume)
        worker.start()
        self.assertTrue(self.process.sent_event.wait(timeout=1))
        self.client.halt()
        self.process.reply(self.process.delayed)
        worker.join(timeout=1)
        self.assertFalse(worker.is_alive())
        self.assertEqual(errors, [])
        self.assertTrue(self.client.halted.is_set())
        with self.assertRaises(RuntimeError):
            self.client.request("perform", action="collect")
        self.assertEqual([r["cmd"] for r in self.process.sent], ["resume"])

    def test_halt_does_not_wait_for_pending_request(self):
        self.process.delay_command = "state"
        errors = []
        def request():
            try:
                self.client.request("state")
            except Exception as exc:
                errors.append(exc)
        worker = threading.Thread(target=request)
        worker.start()
        self.assertTrue(self.process.sent_event.wait(timeout=1))
        start = time.monotonic()
        self.client.halt()
        self.assertLess(time.monotonic() - start, 0.2)
        self.process.reply(self.process.delayed)
        worker.join(timeout=1)
        self.assertFalse(worker.is_alive())
        self.assertFalse(errors)
        self.assertTrue(self.client.halted.is_set())

    def test_reserved_fields_cannot_bypass_mutation_gate(self):
        self.client.halt()
        with self.assertRaises(ValueError):
            self.client.request("state", cmd="perform", action="collect")
        self.assertEqual(self.process.sent, [])

    def test_failed_and_closed_connections_never_send_mutations(self):
        self.client.failed = True
        with self.assertRaises(RuntimeError):
            self.client.request("perform", action="collect")
        self.assertEqual(self.process.sent, [])
        self.client.failed = False
        self.client.close()
        before = len(self.process.sent)
        with self.assertRaises(RuntimeError):
            self.client.request("write", address=123, value=999, approved=True)
        self.assertEqual(len(self.process.sent), before)
        self.assertTrue(self.process.stdin.closed)
        self.assertTrue(self.process.stdout.closed)
        self.assertFalse(self.client.reader.is_alive())

    def test_frozen_default_allows_slow_onefile_child_start(self):
        self.client.close()
        with patch("vision_agent.replica_client.sys.frozen", True, create=True):
            self.process = FakeProcess()
            with patch("vision_agent.replica_client.subprocess.Popen", return_value=self.process):
                self.client = LabClient()
        self.assertEqual(self.client.timeout, 30)


if __name__ == "__main__":
    unittest.main()
