"""Real Tk + real screenshot smoke test. Requires an interactive display or Xvfb."""
import io
import time
import tkinter as tk
from PIL import Image
from vision_agent.ui import App
from vision_agent.core import Decision

root = tk.Tk()
app = App(root)
root.update()
image = Image.open(io.BytesIO(app.capture()))
assert image.width > 100 and image.height > 50
assert len(image.getcolors(maxcolors=1000000)) > 2, "Screenshot must contain rendered content"
app.target.set("43")
app.start()
deadline = time.monotonic() + 12
while app.active and time.monotonic() < deadline:
    root.update()
    time.sleep(0.02)
assert app.lab.value == 43 and not app.active, app.status.get()
assert app.calls == 3, app.calls
# Simulate a late network reply after the stop button.
app.lab.reset(20)
app.active = True
app.generation += 1
old_generation = app.generation
app.stop()
app.results.put((old_generation, app.lab.revision, Decision(20, "add", 10, "late"), None))
app.poll()
assert app.lab.value == 20
# Approval is explicit and scoped to one mutation.
app.lab.stopped.clear()
app.active = True
app.run_target = 70
app.pending = (Decision(20, "set", 70, "Test approval"), app.lab.revision)
app.approve()
assert app.lab.value == 70 and not app.active
# Stopped pending action cannot be approved.
app.lab.reset(20)
app.active = True
app.pending = (Decision(20, "set", 70, "Stop test"), app.lab.revision)
app.stop()
app.approve()
assert app.lab.value == 20
root.update()
app.render()
root.update()
Image.open(io.BytesIO(app.capture())).save("/tmp/vision-agent-smoke.png")
# Explicit alternate capture region works as well.
c = app.canvas
app.region = (c.winfo_rootx(), c.winfo_rooty(), c.winfo_rootx() + 200, c.winfo_rooty() + 100)
assert Image.open(io.BytesIO(app.capture())).size == (200, 100)
app.region = None
# Budget exhaustion is independent of model suggestions.
app.active = True
app.calls = app.run_budget
app.next_step()
assert not app.active
# Failure returned by a worker stops the loop without changing the value.
app.lab.stopped.clear()
app.active = True
app.generation += 1
app.results.put((app.generation, app.lab.revision, None, "Simulated provider failure"))
app.poll()
assert not app.active and app.lab.value == 20
# Full window screenshot for reviewing the interface, not just the capture field.
from PIL import ImageGrab
root.update()
ImageGrab.grab(bbox=(root.winfo_rootx(), root.winfo_rooty(),
                    root.winfo_rootx() + root.winfo_width(), root.winfo_rooty() + root.winfo_height())).save("/tmp/vision-agent-ui.png")
app.close()
print("GUI PASS: real screenshot, demo 20→43, late reply discarded, approval, stop")
