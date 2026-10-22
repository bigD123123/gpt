"""Real controller + owned child + screenshots. Explicit demo, no live AI.
Run using the cloud display helper or an interactive desktop.
"""
import io
import json
import time
import threading
import tkinter as tk
from dataclasses import replace
from pathlib import Path
from unittest.mock import patch
from PIL import Image, ImageGrab
from vision_agent.replica_ai import ReplicaDecision, UsageMetadata, analyze as real_analyze
from vision_agent.replica_ui import ReplicaApp


def pump_until(root, condition, seconds=20):
    deadline = time.monotonic() + seconds
    while not condition() and time.monotonic() < deadline:
        root.update()
        time.sleep(0.02)
    assert condition(), "GUI did not reach expected state"


root = tk.Tk()
app = ReplicaApp(root)
root.update()
try:
    app.launch()
    pump_until(root, lambda: app.client is not None or app.rpc_pending == 0)
    assert app.client is not None, app.status.get()
    child_pid = app.client.process.pid
    state = app.client.request("present")
    screenshot = Image.open(io.BytesIO(app.capture(state)))
    assert screenshot.width > 400 and len(screenshot.getcolors(1_000_000)) > 4
    app.start()
    pump_until(root, lambda: app.pending is not None or not app.active)
    assert app.active and app.flow.phase == "approve", app.status.get()
    assert app.run_call_limit == 12
    counts = [step["phase_result"]["count"] for step in app.plan if "count" in step["phase_result"]]
    assert counts == [3, 2, 1], counts
    assert app.client.request("state")["currency"] == 100
    assert app.calls == 5, app.calls
    # Waiting for approval is quiescent and writes nothing automatically.
    calls = app.calls
    root.update()
    assert app.calls == calls
    app.approve()
    pump_until(root, lambda: not app.active)
    assert app.flow.phase == "done", app.status.get()
    pump_until(root, lambda: app.rpc_pending == 0)
    assert app.calls == 6 and app.client.request("state")["currency"] == 999
    assert app.input_tokens == app.output_tokens == app.total_tokens == 0
    assert "6/12 AI-kald/trin" in app.usage_text.get()
    assert app.plan[-1]["decision"]["usage"]["provider"] == "demo"
    assert app.plan[-1]["phase_result"]["event"] == "visual_verification_passed"
    # Preserve a screenshot of a completed scan for human visual inspection.
    app.root.lift()
    root.update()
    ImageGrab.grab(bbox=(root.winfo_rootx(), root.winfo_rooty(), root.winfo_rootx()+root.winfo_width(), root.winfo_rooty()+root.winfo_height())).save("/tmp/replica-automation-ui.png")
    # Stop while a result is in flight, then inject the late reply.
    app.reset()
    pump_until(root, lambda: app.rpc_pending == 0)
    app.active = True
    app.generation += 1
    old_generation = app.generation
    app.stop()
    app.results.put((old_generation, ReplicaDecision(
        "currency", 50, "collect", "late", UsageMetadata("openai", 11, 3, 14)
    ), None))
    app.poll()
    pump_until(root, lambda: app.rpc_pending == 0)
    assert app.client.request("state")["currency"] == 50
    assert (app.input_tokens, app.output_tokens, app.total_tokens) == (11, 3, 14)
    assert "11 inputtokens" in app.usage_text.get()
    # Reject an actual ready-to-write run.
    app.start()
    pump_until(root, lambda: app.pending is not None or not app.active)
    assert app.pending is not None
    app.reject()
    app.approve()
    pump_until(root, lambda: app.rpc_pending == 0)
    assert not app.active and app.client.request("state")["currency"] == 100
    # A manual mutation during the approval wait invalidates permission.
    app.reset()
    pump_until(root, lambda: app.rpc_pending == 0)
    app.start()
    pump_until(root, lambda: app.pending is not None or not app.active)
    assert app.pending is not None
    app.client.request("perform", action="collect")
    app.approve()
    pump_until(root, lambda: not app.active and app.rpc_pending == 0)
    assert app.client.request("state")["currency"] == 125
    # An existing live-model failure must never switch into demo automatically.
    app.reset()
    pump_until(root, lambda: app.rpc_pending == 0)
    app.provider.set("OpenAI")
    with patch("vision_agent.replica_ui.messagebox.askokcancel", return_value=True), patch("vision_agent.replica_ui.analyze", side_effect=TimeoutError()):
        app.start()
        pump_until(root, lambda: not app.active)
    pump_until(root, lambda: app.rpc_pending == 0)
    assert app.client.request("state")["currency"] == 50
    # A slow target response must not block Tk/STOP or allow a later action.
    app.reset()
    pump_until(root, lambda: app.rpc_pending == 0)
    app.provider.set("Demo (ingen AI)")
    entered, release = threading.Event(), threading.Event()
    original_request = app.client.request
    def slow_request(command, **fields):
        if command == "state" and not entered.is_set():
            entered.set()
            release.wait(timeout=3)
        return original_request(command, **fields)
    with patch.object(app.client, "request", side_effect=slow_request):
        app.start()
        pump_until(root, entered.is_set)
        started = time.monotonic()
        app.stop()
        root.update()
        assert time.monotonic() - started < 0.2, "STOP blocked on a target RPC"
        assert not app.active and app.client.halted.is_set()
        release.set()
        pump_until(root, lambda: app.rpc_pending == 0)
    assert app.client.request("state")["currency"] == 50
    # An explicit call cap stops before another screenshot/model request.
    app.reset()
    pump_until(root, lambda: app.rpc_pending == 0)
    app.call_limit.set("1")
    app.start()
    pump_until(root, lambda: not app.active)
    assert app.calls == 1 and app.client.request("state")["currency"] == 50
    app.call_limit.set("12")
    # A metered provider path must update the UI and persist JSON-safe usage.
    app.reset()
    pump_until(root, lambda: app.rpc_pending == 0)
    app.provider.set("OpenAI")
    def metered_decision(_provider, _model, png, phase, locked, state):
        decision = real_analyze("Demo (ingen AI)", "", png, phase, locked, state)
        return replace(decision, usage=UsageMetadata("openai", 9, 2, 11))
    with patch("vision_agent.replica_ui.messagebox.askokcancel", return_value=True), \
            patch("vision_agent.replica_ui.analyze", side_effect=metered_decision):
        app.start()
        pump_until(root, lambda: app.pending is not None or not app.active)
        assert app.pending is not None
        app.approve()
        pump_until(root, lambda: not app.active)
    pump_until(root, lambda: app.rpc_pending == 0)
    assert (app.calls, app.input_tokens, app.output_tokens, app.total_tokens) == (6, 54, 12, 66)
    assert all(step.get("decision", {}).get("usage", {}).get("provider") == "openai"
               for step in app.plan if "decision" in step)
    plan_path = Path(app.logger.handlers[0].baseFilename).with_name("last-plan.json")
    saved_plan = json.loads(plan_path.read_text(encoding="utf-8"))
    assert saved_plan["actions"][-1]["decision"]["usage"]["total_tokens"] == 11
    for handler in app.logger.handlers:
        handler.flush()
    events = [json.loads(line) for line in Path(app.logger.handlers[0].baseFilename).read_text(encoding="utf-8").splitlines()]
    completed = next(event for event in reversed(events) if event["event"] == "replica_completed")
    assert (completed["input_tokens"], completed["output_tokens"], completed["total_tokens"]) == (54, 12, 66)
    print("REPLICA GUI PASS: child IPC, screenshot, scan3->2->1, approve->999, final image, late reply, reject, stale approval, provider failure")
finally:
    client = app.client
    app.close()
    if client:
        client.process.wait(timeout=5)
        assert client.process.poll() is not None, "Owned test child must terminate on close"
