"""Tk main thread owns screenshots, state, approvals and all actions.
Only the bounded model request runs in a daemon thread. Late replies are discarded.
"""
import hashlib
import io
import queue
import threading
import tkinter as tk
from tkinter import ttk, messagebox, font as tkfont
import sys
from urllib.error import HTTPError, URLError
from PIL import ImageGrab
from .core import TestLab
from .providers import decide, store_api_key
from .audit import audit_logger, log_event, data_dir

PROVIDERS = ("Demo (ingen AI)", "OpenAI", "Ollama (lokal)")


class App:
    def __init__(self, root):
        self.root = root
        self.font_family = "Segoe UI" if sys.platform == "win32" else "Helvetica"
        for name in ("TkDefaultFont", "TkTextFont", "TkMenuFont", "TkHeadingFont"):
            tkfont.nametofont(name).configure(family=self.font_family, size=10)
        root.title("VisionAgent - kontrolleret testmiljø")
        root.geometry("940x790")
        root.minsize(820, 700)
        self.lab = TestLab()
        self.logger = audit_logger()
        self.results = queue.Queue()
        self.active = False
        self.inflight = False
        self.generation = 0
        self.pending = None
        self.region = None
        self.calls = 0
        self.after_id = None
        self.provider = tk.StringVar(value=PROVIDERS[0])
        self.model = tk.StringVar(value="gpt-4.1-mini")
        self.target = tk.StringVar(value="70")
        self.budget = tk.StringVar(value="12")
        self.status = tk.StringVar(value="Klar. Demo bruger faste regler og kræver ingen API-nøgle.")
        self.region_text = tk.StringVar(value="Billedområde: det indbyggede testprogram")
        self._build()
        root.bind("<Escape>", lambda _: self.stop())
        root.protocol("WM_DELETE_WINDOW", self.close)
        root.after(100, self.poll)
        self.event("session_started")

    def _build(self):
        body = ttk.Frame(self.root, padding=15)
        body.pack(fill="both", expand=True)
        ttk.Label(body, text="VisionAgent", font=(self.font_family, 21, "bold")).pack(anchor="w")
        ttk.Label(body, text="Observer -> Analyser -> Beslut -> Udfør -> Kontrollér").pack(anchor="w", pady=(0, 10))
        settings = ttk.LabelFrame(body, text="Indstillinger", padding=10)
        settings.pack(fill="x")
        ttk.Label(settings, text="Beslutningssystem").grid(row=0, column=0, sticky="w")
        self.provider_box = ttk.Combobox(settings, textvariable=self.provider, values=PROVIDERS, state="readonly", width=23)
        self.provider_box.grid(row=0, column=1, padx=8)
        self.provider_box.bind("<<ComboboxSelected>>", self.change_provider)
        ttk.Label(settings, text="Model").grid(row=0, column=2)
        self.model_entry = ttk.Entry(settings, textvariable=self.model, width=25)
        self.model_entry.grid(row=0, column=3, padx=8)
        ttk.Button(settings, text="API-nøgle...", command=self.key_dialog).grid(row=0, column=4)
        ttk.Label(settings, text="Mål (0-100)").grid(row=1, column=0, sticky="w", pady=8)
        self.target_entry = ttk.Entry(settings, textvariable=self.target, width=8)
        self.target_entry.grid(row=1, column=1, sticky="w", padx=8)
        ttk.Label(settings, text="Maks. trin/kald (1-30)").grid(row=1, column=2)
        self.budget_entry = ttk.Entry(settings, textvariable=self.budget, width=8)
        self.budget_entry.grid(row=1, column=3, sticky="w", padx=8)
        area = ttk.Frame(body)
        area.pack(fill="x", pady=8)
        ttk.Button(area, text="Vælg skærmområde...", command=self.select_region).pack(side="left")
        ttk.Button(area, text="Brug testområdet", command=self.default_region).pack(side="left", padx=8)
        ttk.Label(area, textvariable=self.region_text).pack(side="left")
        self.canvas = tk.Canvas(body, height=145, background="#14243b", highlightthickness=0)
        self.canvas.pack(fill="x", pady=6)
        self.canvas.bind("<Configure>", lambda _: self.render())
        self.render()
        controls = ttk.Frame(body)
        controls.pack(fill="x", pady=8)
        self.start_button = ttk.Button(controls, text="Start agent", command=self.start)
        self.start_button.pack(side="left")
        tk.Button(controls, text="STOP (Esc)", command=self.stop, bg="#b52c35", fg="white", padx=20).pack(side="left", padx=10)
        ttk.Button(controls, text="Nulstil testværdi til 20", command=self.reset).pack(side="left")
        ttk.Label(body, textvariable=self.status, wraplength=870).pack(anchor="w", pady=7)
        approval = ttk.LabelFrame(body, text="Godkendelse af direkte værdiændring (set)", padding=8)
        approval.pack(fill="x", pady=5)
        self.approval_text = tk.StringVar(value="Ingen handling afventer godkendelse.")
        ttk.Label(approval, textvariable=self.approval_text, wraplength=700).pack(side="left", fill="x", expand=True)
        self.approve_button = ttk.Button(approval, text="Godkend", command=self.approve, state="disabled")
        self.approve_button.pack(side="left", padx=6)
        self.reject_button = ttk.Button(approval, text="Afvis og stop", command=self.reject, state="disabled")
        self.reject_button.pack(side="left")
        ttk.Label(body, text="Observationer, beslutninger og kontrolresultater").pack(anchor="w", pady=(10, 3))
        frame = ttk.Frame(body)
        frame.pack(fill="both", expand=True)
        self.output = tk.Text(frame, height=12, wrap="word", state="disabled", font=("Consolas" if sys.platform == "win32" else "Courier", 10))
        scroll = ttk.Scrollbar(frame, command=self.output.yview)
        self.output.configure(yscrollcommand=scroll.set)
        scroll.pack(side="right", fill="y")
        self.output.pack(fill="both", expand=True)
        ttk.Label(body, text=f"Log: {data_dir() / 'events.jsonl'}", wraplength=880).pack(anchor="w", pady=(6, 0))

    def render(self):
        c = self.canvas
        c.delete("all")
        w = max(c.winfo_width(), 400)
        c.create_text(w / 2, 22, text="LOCAL TEST LAB — CURRENT VALUE", fill="#a9c5ef", font=(self.font_family, 14))
        c.create_text(w / 2, 78, text=str(self.lab.value), fill="white", font=(self.font_family, 40, "bold"))
        c.create_text(w / 2, 128, text="Only this counter can be changed by the agent", fill="#a9c5ef", font=(self.font_family, 11))

    def event(self, event, **fields):
        log_event(self.logger, event, **fields)
        if hasattr(self, "output"):
            self.output.configure(state="normal")
            self.output.insert("end", event + ": " + str(fields) + "\n")
            # Bound UI memory; disk log rotates separately.
            if int(self.output.index("end-1c").split(".")[0]) > 500:
                self.output.delete("1.0", "100.0")
            self.output.see("end")
            self.output.configure(state="disabled")

    def change_provider(self, _=None):
        self.model.set("llama3.2-vision" if self.provider.get() == PROVIDERS[2] else "gpt-4.1-mini")

    def key_dialog(self):
        self.stop()
        dialog = tk.Toplevel(self.root)
        dialog.title("API-nøgle - Windows Credential Manager")
        dialog.transient(self.root)
        ttk.Label(dialog, text="Nøglen gemmes i operativsystemets nøglelager.\nDen vises ikke i loggen. Tomt felt sletter den gemte nøgle.", padding=12).pack()
        entry = ttk.Entry(dialog, show="*", width=55)
        entry.pack(padx=12, pady=8)
        def save():
            try:
                store_api_key(entry.get())
            except Exception:
                messagebox.showerror("Nøglelager", "Kunne ikke gemme/slette nøglen. Kontrollér operativsystemets nøglelager.", parent=dialog)
                return
            entry.delete(0, "end")
            dialog.destroy()
            self.status.set("Nøglelager opdateret. VISION_API_KEY i miljøet har forrang, hvis den findes.")
        ttk.Button(dialog, text="Gem i nøglelager", command=save).pack(pady=12)

    def default_region(self):
        self.stop()
        self.region = None
        self.region_text.set("Billedområde: det indbyggede testprogram")

    def select_region(self):
        self.stop()
        # Primary display overlay; Pillow uses screen coordinates, with DPI awareness on Windows.
        overlay = tk.Toplevel(self.root)
        overlay.attributes("-fullscreen", True)
        overlay.attributes("-topmost", True)
        overlay.attributes("-alpha", 0.35)
        overlay.configure(bg="black")
        canvas = tk.Canvas(overlay, bg="black", cursor="crosshair", highlightthickness=0)
        canvas.pack(fill="both", expand=True)
        canvas.create_text(30, 30, anchor="nw", fill="white", text="Træk et område på primærskærmen. Esc annullerer.", font=(self.font_family, 16))
        origin = []
        rectangle = []
        def press(e):
            origin[:] = [e.x_root, e.y_root]
            rectangle[:] = [canvas.create_rectangle(e.x, e.y, e.x, e.y, outline="red", width=3)]
        def motion(e):
            if origin and rectangle:
                canvas.coords(rectangle[0], origin[0] - overlay.winfo_rootx(), origin[1] - overlay.winfo_rooty(), e.x, e.y)
        def release(e):
            if not origin:
                return
            x1, x2 = sorted((origin[0], e.x_root))
            y1, y2 = sorted((origin[1], e.y_root))
            if x2 - x1 < 20 or y2 - y1 < 20:
                overlay.destroy()
                return
            self.region = (x1, y1, x2, y2)
            self.region_text.set(f"Billedområde: {self.region}")
            overlay.destroy()
        canvas.bind("<ButtonPress-1>", press)
        canvas.bind("<B1-Motion>", motion)
        canvas.bind("<ButtonRelease-1>", release)
        overlay.bind("<Escape>", lambda e: overlay.destroy())
        overlay.focus_force()

    def settings_enabled(self, enabled):
        self.provider_box.configure(state="readonly" if enabled else "disabled")
        for widget in (self.model_entry, self.target_entry, self.budget_entry, self.start_button):
            widget.configure(state="normal" if enabled else "disabled")

    def start(self):
        if self.active:
            return
        if self.inflight:
            self.status.set("Det tidligere AI-kald afsluttes. Prøv igen om et øjeblik (højst ca. 30 sekunder).")
            return
        try:
            target, budget = int(self.target.get()), int(self.budget.get())
            if not 0 <= target <= 100 or not 1 <= budget <= 30 or not self.model.get().strip():
                raise ValueError()
        except ValueError:
            messagebox.showerror("Indstillinger", "Angiv mål 0-100, maksimalt 1-30 trin og et modelnavn.")
            return
        provider = self.provider.get()
        if provider == "OpenAI" and not messagebox.askokcancel("Deling med OpenAI", "Det valgte skærmområde sendes til OpenAI for hvert trin.\nKontrollér, at det ikke indeholder private oplysninger.\nAPI-brug kan koste penge. Fortsæt?"):
            return
        self.run_target, self.run_budget = target, budget
        self.run_provider, self.run_model = provider, self.model.get().strip()
        self.generation += 1
        self.active = True
        self.lab.stopped.clear()
        self.calls = 0
        self.settings_enabled(False)
        self.event("agent_started", provider=provider, target=target, max_steps=budget)
        self.next_step()

    def stop(self):
        was_active = self.active
        self.lab.stop()
        self.active = False
        self.generation += 1
        self.pending = None
        if self.after_id is not None:
            self.root.after_cancel(self.after_id)
            self.after_id = None
        self.approve_button.configure(state="disabled")
        self.reject_button.configure(state="disabled")
        self.approval_text.set("Ingen handling afventer godkendelse.")
        self.settings_enabled(True)
        self.status.set("Stoppet. Eventuelle sene AI-svar bliver ignoreret.")
        if was_active:
            self.event("agent_stopped")

    def reset(self):
        self.stop()
        self.lab.reset(20)
        self.render()
        self.event("test_reset", value=20)

    def capture(self):
        if self.region:
            box = self.region
        else:
            c = self.canvas
            box = (c.winfo_rootx(), c.winfo_rooty(), c.winfo_rootx() + c.winfo_width(), c.winfo_rooty() + c.winfo_height())
        image = ImageGrab.grab(bbox=box)
        if image.width < 1 or image.height < 1:
            raise ValueError("Tomt skærmområde")
        image.thumbnail((1280, 1280))
        buffer = io.BytesIO()
        image.save(buffer, format="PNG")
        return buffer.getvalue()

    def next_step(self):
        self.after_id = None
        if not self.active:
            return
        if self.calls >= self.run_budget:
            self.stop()
            self.status.set("Tringrænsen er nået. Agenten er stoppet.")
            self.event("step_limit_reached", steps=self.calls)
            return
        try:
            png = self.capture()
        except Exception:
            self.stop()
            self.status.set("Skærmbillede fejlede. Sørg for et synligt skrivebord og et gyldigt område.")
            self.event("capture_failed")
            return
        generation, revision, value = self.generation, self.lab.revision, self.lab.value
        self.calls += 1
        self.inflight = True
        self.event("observation", step=self.calls, image_sha256=hashlib.sha256(png).hexdigest(), revision=revision)
        self.status.set(f"Analyserer og beslutter ... trin {self.calls}/{self.run_budget}")
        provider, model, target = self.run_provider, self.run_model, self.run_target
        def worker():
            try:
                decision = decide(provider, model, png, target, value)
                self.results.put((generation, revision, decision, None))
            except Exception as exc:
                # Never log raw response bodies, keys, URLs with credentials or image content.
                if isinstance(exc, HTTPError):
                    error = f"AI-serveren returnerede HTTP {exc.code}. Kontrollér model, nøgle, adgang og kvote."
                elif isinstance(exc, URLError):
                    error = "AI-forbindelsen fejlede. Kontrollér netværk eller lokal Ollama-server."
                elif isinstance(exc, ValueError):
                    error = "AI-konfigurationen eller svarets format er ugyldigt. Kontrollér nøgle, model og billedindhold."
                else:
                    error = "AI-kaldet fejlede eller fik timeout. Ingen handling udført."
                self.results.put((generation, revision, None, error))
        threading.Thread(target=worker, daemon=True).start()

    def poll(self):
        try:
            generation, revision, decision, error = self.results.get_nowait()
        except queue.Empty:
            pass
        else:
            self.inflight = False
            if self.active and generation == self.generation:
                if error:
                    self.stop()
                    self.status.set(error)
                    self.event("model_error", message=error)
                else:
                    self.event("decision", **decision.__dict__)
                    if decision.action == "set":
                        self.pending = (decision, revision)
                        self.approval_text.set(f"AI aflæste {decision.observed_value} og vil sætte værdien til {decision.amount}. {decision.reason}")
                        self.approve_button.configure(state="normal")
                        self.reject_button.configure(state="normal")
                        self.status.set("Afventer godkendelse. Ingen nye AI-kald imens.")
                    else:
                        self.execute(decision, revision)
            else:
                self.event("late_response_discarded")
        self.root.after(100, self.poll)

    def approve(self):
        if self.pending and self.active:
            decision, revision = self.pending
            self.pending = None
            self.approve_button.configure(state="disabled")
            self.reject_button.configure(state="disabled")
            self.approval_text.set("Godkendelsen er brugt til én handling.")
            self.event("action_approved", action=decision.action, amount=decision.amount)
            self.execute(decision, revision, True)

    def reject(self):
        self.event("action_rejected")
        self.stop()

    def execute(self, decision, revision, approved=False):
        try:
            result = self.lab.apply(decision, revision, self.run_target, approved)
        except (ValueError, PermissionError, RuntimeError) as exc:
            self.stop()
            self.status.set(str(exc))
            self.event("action_blocked", message=str(exc))
            return
        self.render()
        self.event("result_verified", **result)
        if result["goal_reached"]:
            self.stop()
            self.status.set(f"Målet er nået og kontrolleret: {result['after']}.")
            self.event("goal_reached", value=result["after"])
        else:
            self.status.set(f"Kontrolleret: {result['before']} -> {result['after']}. Nyt billede om 1 sekund.")
            self.after_id = self.root.after(1000, self.next_step)

    def close(self):
        self.stop()
        self.event("session_closed")
        self.root.destroy()


def main():
    if "--packaged-smoke" in sys.argv:
        from .packaged_smoke import run as packaged_smoke
        raise SystemExit(packaged_smoke())
    if "--test-game" in sys.argv:
        from .test_game import main as game_main
        game_main()
        return
    if sys.platform == "win32":
        import ctypes
        try:
            ctypes.windll.shcore.SetProcessDpiAwareness(2)
        except (OSError, AttributeError):
            pass
    root = tk.Tk()
    if "--classic" in sys.argv:
        App(root)
    else:
        from .replica_ui import ReplicaApp
        ReplicaApp(root)
    root.mainloop()
