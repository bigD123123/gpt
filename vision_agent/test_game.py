"""Own, separately launched Tk test game with a bounded JSON Lines RPC API.

Request: {"id": integer, "cmd": command, ...arguments}
Success: {"id": same, "ok": true, "result": object}
Failure: {"id": same, "ok": false, "error": human-readable string}
Ready: {"event": "ready", "state": state}

Commands: state, present, snapshot, perform(action,revision), write(address,value,revision,approved),
stop, resume, reset, close. Only this process's OwnedLab buffer is accessible.
State adds screen-coordinate canvas `bounds`, an owned top-level Windows
`window_handle` (None elsewhere), `window_title`, and `actions` with
canvas-relative clickable rectangles. No external windows/processes are touched.
"""
import json
import os
import queue
import sys
import threading
import tkinter as tk
from tkinter import font as tkfont, ttk
from .replica_lab import OwnedLab


class TestGame:
    def __init__(self, root, instream=None, outstream=None):
        self.root = root
        self.lab = OwnedLab()
        self.requests = queue.Queue()
        self.instream = instream if instream is not None else sys.stdin
        self.outstream = outstream if outstream is not None else sys.stdout
        self.window_title = f"VisionAgent Test Lab [{os.getpid()}]"
        root.title(self.window_title)
        target_x = max(0, root.winfo_screenwidth() - 610)
        root.geometry(f"590x480+{target_x}+25")
        root.minsize(590, 480)
        root.configure(bg="#0c1626")
        self.font = "Segoe UI" if sys.platform == "win32" else "Helvetica"
        for name in ("TkDefaultFont", "TkTextFont", "TkMenuFont", "TkHeadingFont"):
            tkfont.nametofont(name).configure(family=self.font, size=10)
        self.canvas = tk.Canvas(root, width=570, height=345, bg="#14243b", highlightthickness=0)
        self.canvas.pack(fill="both", expand=True, padx=10, pady=(10, 0))
        self.actions = {}
        self.buttons = []
        self.info = tk.StringVar(value="Ejet testprogram. Kun disse testværdier kan ændres.")
        ttk.Label(root, textvariable=self.info, wraplength=560).pack(fill="x", padx=10, pady=7)
        self.canvas.bind("<Configure>", lambda _: self.render())
        self.canvas.bind("<Button-1>", self.click)
        root.bind("<Escape>", lambda _: self.stop())
        root.protocol("WM_DELETE_WINDOW", self.close)
        root.update_idletasks()
        self.render()
        self.root.after(20, self.poll)

    def emit(self, obj):
        try:
            self.outstream.write(json.dumps(obj, ensure_ascii=True) + "\n")
            self.outstream.flush()
        except (BrokenPipeError, OSError, ValueError):
            self.close()

    def state(self):
        c = self.canvas
        window_handle = None
        if sys.platform == "win32":
            # Tk can expose its inner client HWND. Resolve only that owned HWND
            # to its top-level wrapper for Pillow's window-specific capture.
            import ctypes
            from ctypes import wintypes
            get_ancestor = ctypes.WinDLL("user32", use_last_error=True).GetAncestor
            get_ancestor.argtypes = [wintypes.HWND, wintypes.UINT]
            get_ancestor.restype = wintypes.HWND
            owned_hwnd = self.root.winfo_id()
            window_handle = int(get_ancestor(owned_hwnd, 2) or owned_hwnd)
        return {
            **self.lab.state(),
            "bounds": [c.winfo_rootx(), c.winfo_rooty(), c.winfo_rootx() + c.winfo_width(), c.winfo_rooty() + c.winfo_height()],
            "window_handle": window_handle,
            "window_title": self.window_title,
            "actions": dict(self.actions),
        }

    def render(self):
        c = self.canvas
        c.delete("all")
        w, h = max(c.winfo_width(), 570), max(c.winfo_height(), 345)
        state = self.lab.state()
        c.create_text(w/2, 28, text="VISION LAB - OWNED TEST GAME", fill="#a8c5ef", font=(self.font, 15, "bold"))
        c.create_text(w/4, 80, text="CURRENCY", fill="#f6cc65", font=(self.font, 13))
        c.create_text(w*3/4, 80, text="AMMO", fill="#93caff", font=(self.font, 13))
        c.create_text(w/4, 125, text=str(state["currency"]), fill="white", font=(self.font, 32, "bold"))
        c.create_text(w*3/4, 125, text=str(state["ammo"]), fill="white", font=(self.font, 32, "bold"))
        specs = [
            ("collect", "Collect sun +25", "#a17e29"),
            ("spend", "Buy seed -10", "#3b795b"),
            ("fire", "Fire shot -1", "#8f4552"),
            ("reload", "Reload +5", "#386d9c"),
        ]
        self.actions = {}
        for i, (action, label, color) in enumerate(specs):
            column, row = i % 2, i // 2
            x1, x2 = 22 + column * (w/2), (w/2 - 22) + column*(w/2)
            y1, y2 = 185 + row*60, 230 + row*60
            self.actions[action] = [int(x1), int(y1), int(x2), int(y2)]
            c.create_rectangle(x1, y1, x2, y2, fill="#39414e" if state["stopped"] else color, outline="")
            c.create_text((x1+x2)/2, (y1+y2)/2, text=label, fill="white", font=(self.font, 12))
        c.create_text(w/2, min(h-20, 335), text="STOPPED" if state["stopped"] else "Documented actions only - Esc stops this target", fill="#b2c4d9", font=(self.font, 10))

    def click(self, event):
        for action, (x1, y1, x2, y2) in self.actions.items():
            if x1 <= event.x <= x2 and y1 <= event.y <= y2:
                try:
                    self.lab.perform(action)
                    self.info.set("Manuel handling: " + action)
                except ValueError as exc:
                    self.info.set(str(exc))
                self.render()
                return

    def stop(self):
        self.lab.stop()
        self.info.set("Testprogrammet er stoppet. Agenten skal starte en ny kørsel.")
        self.render()

    def reader(self):
        for line in self.instream:
            if len(line) > 20_000:
                self.requests.put({"id": None, "cmd": "invalid"})
                continue
            try:
                request = json.loads(line)
            except (ValueError, TypeError):
                request = {"id": None, "cmd": "invalid"}
            self.requests.put(request)
        self.requests.put({"id": None, "cmd": "close"})

    def dispatch(self, request):
        if not isinstance(request, dict):
            raise ValueError("Ugyldig forespørgsel")
        cmd = request.get("cmd")
        if cmd == "state":
            return self.state()
        if cmd == "present":
            # Raise only the owned target, never an arbitrary desktop window.
            # Processing pending window-system events makes mapping/painting
            # complete before the controller receives the capture coordinates.
            self.root.deiconify()
            self.root.lift()
            self.root.update_idletasks()
            self.root.update()
            return self.state()
        if cmd == "snapshot":
            return self.lab.snapshot()
        if cmd == "perform":
            self.lab.perform(request.get("action"), request.get("revision"))
        elif cmd == "write":
            self.lab.write(request.get("address"), request.get("value"), request.get("revision"), request.get("approved", False))
        elif cmd == "stop":
            self.stop()
        elif cmd == "resume":
            self.lab.resume()
        elif cmd == "reset":
            self.lab.reset()
        elif cmd == "close":
            return {"closed": True}
        else:
            raise ValueError("Ukendt kommando")
        self.render()
        self.root.update_idletasks()
        return self.state()

    def poll(self):
        # Execute every mutation on Tk's main thread; the pipe reader never acts.
        try:
            request = self.requests.get_nowait()
        except queue.Empty:
            self.root.after(20, self.poll)
            return
        request_id = request.get("id") if isinstance(request, dict) else None
        try:
            result = self.dispatch(request)
            self.emit({"id": request_id, "ok": True, "result": result})
        except (ValueError, TypeError, PermissionError) as exc:
            self.emit({"id": request_id, "ok": False, "error": str(exc)})
        if isinstance(request, dict) and request.get("cmd") == "close":
            self.close()
            return
        self.root.after(20, self.poll)

    def run(self):
        self.root.update()
        self.emit({"event": "ready", "state": self.state()})
        threading.Thread(target=self.reader, daemon=True).start()
        self.root.mainloop()

    def close(self):
        self.lab.stop()
        try:
            self.root.destroy()
        except tk.TclError:
            pass


def main():
    if sys.platform == "win32":
        import ctypes
        try:
            ctypes.windll.shcore.SetProcessDpiAwareness(2)
        except (AttributeError, OSError):
            pass
    root = tk.Tk()
    TestGame(root).run()


if __name__ == "__main__":
    main()
