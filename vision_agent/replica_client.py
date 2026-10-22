"""Launch and talk only to the test process created by this app.

No PID input, network listener, shell command or arbitrary process attachment.
All blocking calls belong on the UI's serialized background worker. ``halt`` is
safe to call immediately from the UI thread and does not wait for an RPC reply.
"""
import itertools
import json
import os
from pathlib import Path
import queue
import subprocess
import sys
import threading


class LabClient:
    def __init__(self, timeout=None):
        # A frozen one-file executable must unpack itself again for the owned
        # test child. Defender can make that substantially slower than source.
        self.timeout = (30 if getattr(sys, "frozen", False) else 5) if timeout is None else timeout
        self.messages = queue.Queue()
        self.ids = itertools.count(1)
        self.failed = False
        self.closed = False
        self.halted = threading.Event()
        self.send_lock = threading.Lock()
        self._request_lock = threading.Lock()
        self._halt_generation = 0
        command = ([sys.executable, "--test-game"] if getattr(sys, "frozen", False)
                   else [sys.executable, "-u", "-m", "vision_agent.test_game"])
        environment = os.environ.copy()
        for name in ("VISION_API_KEY", "OPENAI_API_KEY"):
            environment.pop(name, None)
        options = {"creationflags": subprocess.CREATE_NO_WINDOW} if sys.platform == "win32" else {}
        self.process = subprocess.Popen(command, stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                                        stderr=subprocess.DEVNULL, text=True, encoding="utf-8",
                                        bufsize=1, env=environment,
                                        cwd=None if getattr(sys, "frozen", False) else str(Path(__file__).resolve().parent.parent),
                                        **options)
        self.reader = threading.Thread(target=self._read, daemon=True)
        self.reader.start()
        try:
            message = self.messages.get(timeout=self.timeout)
            if not isinstance(message, dict) or message.get("event") != "ready":
                raise RuntimeError("Testprogrammet kunne ikke startes")
            self.initial_state = message["state"]
        except Exception:
            self.failed = True
            self.close()
            raise RuntimeError("Testprogrammet blev ikke klar. Kontrollér at en desktop er tilgængelig.") from None

    def _read(self):
        try:
            while True:
                line = self.process.stdout.readline(1_000_002)
                if not line:
                    return
                if len(line) > 1_000_000:
                    return
                try:
                    self.messages.put(json.loads(line))
                except json.JSONDecodeError:
                    return
        except (OSError, ValueError):
            # Closing a terminated child's pipe must not print a thread traceback.
            return
        finally:
            self.messages.put({"connection_error": True})

    def halt(self):
        """Gate new actions locally without waiting for the child or model.

        A command already sent before this lock is acquired cannot be retracted.
        The UI separately queues ``stop`` to invalidate the target's revision.
        """
        with self.send_lock:
            self.halted.set()
            self._halt_generation += 1

    def _mark_failed(self):
        with self.send_lock:
            self.failed = True
            self.halted.set()
            self._halt_generation += 1

    def request(self, command, **fields):
        if "id" in fields or "cmd" in fields:
            raise ValueError("Reserverede RPC-felter må ikke overskrives")
        if command == "stop":
            self.halt()
        # Only one request consumes the ordered response queue. This lock is not
        # used by halt(), so a slow target cannot delay the UI's local action gate.
        with self._request_lock:
            number = next(self.ids)
            try:
                with self.send_lock:
                    if self.failed or self.closed or self.process.poll() is not None:
                        raise RuntimeError("Forbindelsen til testprogrammet er lukket")
                    if command in ("perform", "write") and self.halted.is_set():
                        raise RuntimeError("Agenten er stoppet; handlingen blev ikke sendt")
                    resume_generation = self._halt_generation
                    self.process.stdin.write(json.dumps({"id": number, "cmd": command, **fields}) + "\n")
                    self.process.stdin.flush()
                message = self.messages.get(timeout=self.timeout)
            except (OSError, ValueError, queue.Empty):
                self._mark_failed()
                raise RuntimeError("Testprogrammet svarede ikke; forløbet er stoppet") from None
            if not isinstance(message, dict) or message.get("id") != number:
                self._mark_failed()
                raise RuntimeError("Uventet svar fra testprogrammet")
            if not message.get("ok"):
                # Fixed errors from our own target, never raw model replies.
                raise ValueError(message.get("error", "Testhandlingen blev afvist"))
            if "result" not in message:
                self._mark_failed()
                raise RuntimeError("Testprogrammet returnerede ikke et resultat")
            if command == "resume":
                with self.send_lock:
                    # A new STOP during an in-flight resume must win even if
                    # that resume eventually succeeds on the target.
                    if self._halt_generation == resume_generation:
                        self.halted.clear()
            return message["result"]

    def close(self):
        self.halt()
        with self.send_lock:
            if self.closed:
                return
        try:
            if self.process.poll() is None and not self.failed:
                try:
                    self.request("close")
                except (ValueError, RuntimeError, OSError):
                    pass
            with self.send_lock:
                self.closed = True
            try:
                self.process.wait(timeout=1)
            except subprocess.TimeoutExpired:
                self.process.terminate()
                try:
                    self.process.wait(timeout=1)
                except subprocess.TimeoutExpired:
                    self.process.kill()
                    self.process.wait(timeout=1)
        finally:
            with self.send_lock:
                self.closed = True
            self.reader.join(timeout=1)
            if self.process.stdin:
                self.process.stdin.close()
            if self.process.stdout:
                self.process.stdout.close()
