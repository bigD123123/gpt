"""Video-inspired automation console, limited to a test target we launch ourselves."""
import hashlib
import io
import json
import queue
import sys
import threading
import tkinter as tk
from tkinter import ttk, messagebox, font as tkfont
from urllib.error import HTTPError, URLError
from PIL import ImageGrab, ImageTk
from .audit import audit_logger, log_event, data_dir
from .providers import store_api_key
from .replica_ai import analyze
from .replica_client import LabClient
from .replica_flow import ReplicaFlow

PROVIDERS = ("Demo (ingen AI)", "OpenAI", "Ollama (lokal)")
STAT_CHOICES = ("Automatisk", "Valuta", "Ammunition")
STAT_VALUES = {"Automatisk": None, "Valuta": "currency", "Ammunition": "ammo"}


class ReplicaApp:
    def __init__(self, root):
        self.root = root
        self.client = None
        self.flow = None
        self.active = False
        self.closed = False
        self.inflight = False
        self.generation = 0
        self.after_id = None
        self.pending = None
        self.results = queue.Queue()
        self.rpc_tasks = queue.Queue()
        self.rpc_results = queue.Queue()
        self.rpc_pending = 0
        self.start_after_launch = False
        self.calls = 0
        self.input_tokens = 0
        self.output_tokens = 0
        self.total_tokens = 0
        self.plan = []
        self.logger = audit_logger()
        self.provider = tk.StringVar(value=PROVIDERS[0])
        self.model = tk.StringVar(value="gpt-5-nano")
        self.desired = tk.StringVar(value="999")
        self.budget = tk.StringVar(value="6")
        self.call_limit = tk.StringVar(value="12")
        self.stat_choice = tk.StringVar(value=STAT_CHOICES[0])
        self.status = tk.StringVar(value="Start det lokale testprogram, og vælg derefter Start AI-forløb.")
        self.usage_text = tk.StringVar(value="Forbrug: 0 AI-kald/trin | 0 inputtokens | 0 outputtokens")
        self.details = tk.StringVar(value="Ingen observation endnu")
        self.approval_text = tk.StringVar(value="Ingen ændring afventer godkendelse.")
        self.preview_image = None
        self.font = "Segoe UI" if sys.platform == "win32" else "Helvetica"
        for name in ("TkDefaultFont", "TkTextFont", "TkHeadingFont", "TkMenuFont"):
            tkfont.nametofont(name).configure(family=self.font, size=10)
        root.title("AI Automation Lab")
        root.geometry("1060x850+25+30")
        root.minsize(930, 760)
        root.configure(bg="#101722")
        self._build()
        root.bind("<Escape>", lambda _: self.stop())
        root.bind("<Key-p>", lambda _: self.start() if not self.active and root.focus_get() not in (self.model_entry, self.value_entry, self.budget_entry, self.call_limit_entry) else None)
        root.protocol("WM_DELETE_WINDOW", self.close)
        threading.Thread(target=self.rpc_worker, daemon=True).start()
        self.root.after(100, self.poll)
        self.event("INFO", "Klar. Demo er regelbaseret; OpenAI/Ollama vælger handlinger ud fra billeder.", kind="session_started")

    def _build(self):
        style = ttk.Style(self.root)
        style.theme_use("clam")
        style.configure("Lab.TFrame", background="#101722")
        style.configure("Lab.TLabel", background="#101722", foreground="#dce7f7")
        style.configure("Lab.TLabelframe", background="#101722", foreground="#dce7f7")
        style.configure("Lab.TLabelframe.Label", background="#101722", foreground="#aabbd2")
        style.configure("Lab.TButton", padding=7)
        style.configure("Lab.TCheckbutton", background="#101722", foreground="#dce7f7")
        body = ttk.Frame(self.root, padding=18, style="Lab.TFrame")
        body.pack(fill="both", expand=True)
        ttk.Label(body, text="AI AUTOMATION LAB", style="Lab.TLabel", font=(self.font, 23, "bold")).pack(anchor="w")
        ttk.Label(body, text="Observer > Første søgning > AI-handling > Ny søgning > Godkend > Kontrollér", style="Lab.TLabel").pack(anchor="w", pady=(2, 14))
        settings = ttk.Frame(body, style="Lab.TFrame")
        settings.pack(fill="x")
        ttk.Label(settings, text="Beslutningssystem", style="Lab.TLabel").grid(row=0, column=0, sticky="w")
        self.provider_box = ttk.Combobox(settings, textvariable=self.provider, values=PROVIDERS, width=21, state="readonly")
        self.provider_box.grid(row=0, column=1, padx=10)
        self.provider_box.bind("<<ComboboxSelected>>", self.change_provider)
        ttk.Label(settings, text="Model", style="Lab.TLabel").grid(row=0, column=2, padx=(5, 0))
        self.model_entry = ttk.Entry(settings, textvariable=self.model, width=23)
        self.model_entry.grid(row=0, column=3, padx=10)
        ttk.Button(settings, text="API-nøgle...", command=self.key_dialog, style="Lab.TButton").grid(row=0, column=4, padx=5)
        ttk.Label(settings, text="Ny testværdi", style="Lab.TLabel").grid(row=1, column=0, sticky="w", pady=12)
        self.value_entry = ttk.Entry(settings, textvariable=self.desired, width=9)
        self.value_entry.grid(row=1, column=1, padx=10, sticky="w")
        ttk.Label(settings, text="Maks. cyklusser (1-12)", style="Lab.TLabel").grid(row=1, column=2, padx=(5, 0))
        self.budget_entry = ttk.Entry(settings, textvariable=self.budget, width=8)
        self.budget_entry.grid(row=1, column=3, padx=10, sticky="w")
        ttk.Label(settings, text="Undersøg", style="Lab.TLabel").grid(row=2, column=0, sticky="w")
        self.stat_box = ttk.Combobox(settings, textvariable=self.stat_choice, values=STAT_CHOICES, width=18, state="readonly")
        self.stat_box.grid(row=2, column=1, padx=10, pady=(0, 10), sticky="w")
        ttk.Label(settings, text="Maks. AI-kald/trin (1-30)", style="Lab.TLabel").grid(row=2, column=2, padx=(5, 0), sticky="w")
        self.call_limit_entry = ttk.Entry(settings, textvariable=self.call_limit, width=8)
        self.call_limit_entry.grid(row=2, column=3, padx=10, pady=(0, 10), sticky="w")
        controls = ttk.Frame(body, style="Lab.TFrame")
        controls.pack(fill="x", pady=(0, 12))
        ttk.Button(controls, text="Start testprogram", command=self.launch, style="Lab.TButton").pack(side="left")
        self.start_button = ttk.Button(controls, text="Start AI-forløb (P)", command=self.start, style="Lab.TButton")
        self.start_button.pack(side="left", padx=8)
        tk.Button(controls, text="STOP (Esc)", command=self.stop, bg="#ba3544", fg="white", activebackground="#df5060", padx=17, pady=6, relief="flat").pack(side="left")
        ttk.Button(controls, text="Nulstil testprogram", command=self.reset, style="Lab.TButton").pack(side="left", padx=8)
        observation = ttk.Frame(body, style="Lab.TFrame")
        observation.pack(fill="x")
        self.preview = tk.Label(observation, bg="#192638", fg="#9cbbdf", text="Skærmbilledet fra dit eget testprogram vises her", width=62, height=8)
        self.preview.pack(side="left", padx=(0, 12))
        ttk.Label(observation, textvariable=self.details, style="Lab.TLabel", wraplength=400, justify="left").pack(side="left", fill="x", expand=True)
        ttk.Label(body, textvariable=self.status, style="Lab.TLabel", wraplength=1000).pack(anchor="w", pady=(12, 3))
        ttk.Label(body, textvariable=self.usage_text, style="Lab.TLabel", wraplength=1000).pack(anchor="w", pady=(0, 12))
        approve = ttk.LabelFrame(body, text="Godkend ændring af den fundne testværdi", style="Lab.TLabelframe", padding=10)
        approve.pack(fill="x", pady=(0, 12))
        ttk.Label(approve, textvariable=self.approval_text, style="Lab.TLabel", wraplength=640).pack(side="left", expand=True, fill="x")
        self.approve_button = ttk.Button(approve, text="Godkend ændring", command=self.approve, state="disabled", style="Lab.TButton")
        self.approve_button.pack(side="left", padx=6)
        self.reject_button = ttk.Button(approve, text="Afvis", command=self.reject, state="disabled", style="Lab.TButton")
        self.reject_button.pack(side="left")
        terminal = ttk.Frame(body, style="Lab.TFrame")
        terminal.pack(fill="both", expand=True)
        self.output = tk.Text(terminal, bg="#0b1018", fg="#bdcce1", insertbackground="white", relief="flat", borderwidth=0, padx=12, pady=12,
                              state="disabled", height=10, wrap="word", font=("Consolas" if sys.platform == "win32" else "Courier", 10))
        scroll = ttk.Scrollbar(terminal, command=self.output.yview)
        self.output.configure(yscrollcommand=scroll.set)
        scroll.pack(side="right", fill="y")
        self.output.pack(fill="both", expand=True)
        for tag, color in (("INFO", "#7fb7f7"), ("OK", "#8ddcaa"), ("WARN", "#edcf7d"), ("ERROR", "#fb9298"), ("AI", "#d0b1ff")):
            self.output.tag_configure(tag, foreground=color)
        ttk.Label(body, text=f"Eget testprogram og registreret testbuffer | Log: {data_dir() / 'events.jsonl'}", style="Lab.TLabel", wraplength=1000).pack(anchor="w", pady=(9, 0))

    def event(self, level, message, kind="replica_event", **fields):
        log_event(self.logger, kind, level=level, message=message, **fields)
        self.output.configure(state="normal")
        self.output.insert("end", f"[{level}] {message}\n", level)
        if int(self.output.index("end-1c").split(".")[0]) > 700:
            self.output.delete("1.0", "150.0")
        self.output.see("end")
        self.output.configure(state="disabled")

    def change_provider(self, _=None):
        self.model.set("llama3.2-vision" if self.provider.get() == PROVIDERS[2] else "gpt-5-nano")

    def key_dialog(self):
        self.stop()
        window = tk.Toplevel(self.root)
        window.title("API-nøgle i operativsystemets nøglelager")
        window.transient(self.root)
        ttk.Label(window, text="Gem din OpenAI API-nøgle i Windows Credential Manager.\nTomt felt sletter den gemte nøgle. VISION_API_KEY har forrang.", padding=15).pack()
        entry = ttk.Entry(window, show="*", width=55)
        entry.pack(padx=15, pady=8)
        def save():
            try:
                store_api_key(entry.get())
            except Exception:
                messagebox.showerror("Nøglelager", "Nøglelageret kunne ikke opdateres. Kontrollér operativsystemets nøglelager.", parent=window)
                return
            entry.delete(0, "end")
            window.destroy()
            self.status.set("Nøglelager opdateret.")
        ttk.Button(window, text="Gem", command=save).pack(pady=12)

    def launch(self):
        if self.active or self.rpc_pending:
            self.status.set("Testprogrammet behandler en forespørgsel. Prøv igen om et øjeblik.")
            return
        if self.client and self.client.process.poll() is None and not self.client.failed and not self.client.closed:
            self.status.set("Testprogrammet kører allerede.")
            return
        previous = self.client
        self.client = None
        def launch_target():
            if previous:
                previous.close()
            return LabClient()
        self.status.set("Starter det lokale testprogram ... Stop er tilgængelig imens.")
        self.submit("launch", launch_target)

    def settings_enabled(self, enabled):
        self.provider_box.configure(state="readonly" if enabled else "disabled")
        self.stat_box.configure(state="readonly" if enabled else "disabled")
        for entry in (self.model_entry, self.value_entry, self.budget_entry, self.call_limit_entry, self.start_button):
            entry.configure(state="normal" if enabled else "disabled")

    def start(self):
        if self.active:
            return
        if self.inflight or self.rpc_pending:
            self.status.set("En tidligere forespørgsel afsluttes. Prøv igen om et øjeblik.")
            return
        try:
            desired, budget, call_limit = int(self.desired.get()), int(self.budget.get()), int(self.call_limit.get())
            preferred_stat = STAT_VALUES[self.stat_choice.get()]
            if (not 0 <= desired <= 1_000_000 or not 1 <= budget <= 12 or
                    not 1 <= call_limit <= 30 or not self.model.get().strip()):
                raise ValueError()
            if preferred_stat == "ammo" and desired > 1000:
                raise ValueError()
        except ValueError:
            messagebox.showerror(
                "Indstillinger",
                "Angiv ny værdi 0-1000000 (ammunition højst 1000), cyklusser 1-12, "
                "AI-kald 1-30 og et modelnavn.",
            )
            return
        if not self.client or self.client.process.poll() is not None or self.client.failed or self.client.closed:
            self.launch()
            self.start_after_launch = True
            return
        if self.provider.get() == "OpenAI" and not messagebox.askokcancel("Billeder sendes til OpenAI", "Billeder af det lokale testprogram sendes til OpenAI.\nHold testvinduet synligt og fri for private oplysninger. API-brug kan koste penge. Fortsæt?"):
            return
        self.flow = ReplicaFlow(self.client, desired, budget, preferred_stat)
        self.run_provider, self.run_model = self.provider.get(), self.model.get().strip()
        self.run_call_limit = call_limit
        self.active = True
        self.generation += 1
        self.calls = 0
        self.input_tokens = 0
        self.output_tokens = 0
        self.total_tokens = 0
        self.plan = []
        self.update_usage_text()
        self.settings_enabled(False)
        self.event(
            "INFO", "Starter nyt observe/scan/handling/rescan-forløb.",
            kind="replica_started", provider=self.run_provider, model=self.run_model,
            value=desired, requested_stat=preferred_stat or "automatic",
            max_cycles=budget, max_calls=call_limit,
        )
        client = self.client
        self.submit("resume", lambda: client.request("resume"))

    def capture(self, state):
        handle = state.get("window_handle")
        if sys.platform == "win32":
            if type(handle) is not int or handle <= 0 or handle != self.client.initial_state.get("window_handle"):
                raise ValueError("Testprogrammets vindue kunne ikke identificeres")
            # PrintWindow through Pillow: only the owned HWND, no desktop rectangle.
            image = ImageGrab.grab(window=handle)
        else:
            bounds = state.get("bounds")
            if not isinstance(bounds, list) or len(bounds) != 4 or any(type(v) is not int for v in bounds):
                raise ValueError("Testprogrammets skærmområde er ugyldigt")
            if bounds[2] <= bounds[0] or bounds[3] <= bounds[1]:
                raise ValueError("Testprogrammet skal være synligt på skærmen")
            image = ImageGrab.grab(bbox=tuple(bounds))
        image.thumbnail((1280, 1280))
        preview = image.copy()
        preview.thumbnail((470, 170))
        self.preview_image = ImageTk.PhotoImage(preview)
        self.preview.configure(image=self.preview_image, text="", width=470, height=170)
        buffer = io.BytesIO()
        image.save(buffer, format="PNG")
        return buffer.getvalue()

    def next_step(self):
        self.after_id = None
        if not self.active:
            return
        if self.flow.phase == "change" and self.flow.cycles >= self.flow.max_cycles:
            self.fail("Cyklusgrænsen er nået. Der blev ikke fundet én entydig kandidat.")
            return
        if self.calls >= self.run_call_limit:
            self.fail("Grænsen for AI-kald/trin er nået. Ingen ny analyse eller handling blev startet.")
            return
        flow, client = self.flow, self.client
        def prepare():
            state = flow.observe()
            presented = client.request("present")
            if presented["session"] != state["session"] or presented["revision"] != state["revision"]:
                raise ValueError("Testværdien blev ændret før skærmbilledet")
            return presented
        self.submit("observation", prepare)

    def begin_model(self, state):
        try:
            png = self.capture(state)
        except (ValueError, RuntimeError, OSError) as exc:
            self.fail(str(exc))
            return
        phase = "verify" if self.flow.phase == "final" else self.flow.phase
        generation = self.generation
        self.inflight = True
        self.calls += 1
        self.update_usage_text()
        self.status.set(
            f"AI observerer: {self.flow.phase}. Kald {self.calls}/{self.run_call_limit}; "
            f"cyklus {self.flow.cycles}/{self.flow.max_cycles}."
        )
        self.event("INFO", f"Tager nyt skærmbillede og analyserer ({self.flow.phase}).", kind="replica_observation", image_sha256=hashlib.sha256(png).hexdigest(), revision=state["revision"], call=self.calls)
        provider, model = self.run_provider, self.run_model
        locked = self.flow.stat_type or self.flow.preferred_stat
        def worker():
            try:
                decision = analyze(provider, model, png, phase, locked, state)
                self.results.put((generation, decision, None))
            except Exception as exc:
                if isinstance(exc, HTTPError):
                    error = f"AI-serveren svarede HTTP {exc.code}. Kontrollér model, nøgle og kvote."
                elif isinstance(exc, URLError):
                    error = "AI-forbindelsen fejlede. Kontrollér netværket eller din lokale Ollama-server."
                elif isinstance(exc, ValueError):
                    error = "AI-kaldet gav ikke et gyldigt svar. Kontrollér model, nøgle, billedindhold og format."
                else:
                    error = "AI-kaldet fejlede eller fik timeout. Ingen ny handling udført."
                self.results.put((generation, None, error))
        threading.Thread(target=worker, daemon=True).start()

    def poll(self):
        try:
            generation, kind, value, error = self.rpc_results.get_nowait()
        except queue.Empty:
            pass
        else:
            self.rpc_pending -= 1
            if generation != self.generation:
                if kind == "launch" and value is not None:
                    # Startup completed after cancellation; clean up our own target.
                    value.halt()
                    if value.process.poll() is None:
                        value.process.terminate()
                    self.submit("cleanup", value.close)
            elif error and kind not in ("stop", "cleanup"):
                self.fail(error)
            elif kind == "launch" and not error:
                self.client = value
                self.status.set("Testprogrammet er startet.")
                self.event("OK", "Eget testprogram startet. Ingen andre processer kan vælges.", kind="owned_target_started")
                if self.start_after_launch:
                    self.start_after_launch = False
                    self.start()
            elif kind == "reset" and not error:
                self.details.set("Nulstillet: currency=50, ammo=12")
                self.status.set("Testprogrammet er nulstillet.")
                self.event("INFO", "Testprogrammet er nulstillet.", kind="replica_reset", revision=value["revision"])
            elif self.active and not error:
                if kind == "resume":
                    self.next_step()
                elif kind == "observation":
                    self.begin_model(value)
                elif kind == "accept":
                    event, decision = value
                    try:
                        self.plan.append({"phase_result": event, "decision": self.decision_record(decision)})
                        self.save_plan()
                        self.present(event)
                        self.after_accept()
                    except (ValueError, RuntimeError, OSError) as exc:
                        self.fail(str(exc))
                elif kind == "write":
                    try:
                        self.plan.append({"phase_result": value})
                        self.save_plan()
                        self.present(value)
                    except OSError as exc:
                        self.fail(str(exc))
                    else:
                        self.status.set("Testbuffer og faktisk værdi kontrolleret. Nyt billede til slutkontrol.")
                        self.after_id = self.root.after(700, self.next_step)
        try:
            generation, decision, error = self.results.get_nowait()
        except queue.Empty:
            pass
        else:
            self.inflight = False
            if decision is not None:
                # A completed provider request can cost tokens even when STOP
                # makes its action stale. Account for it before discarding.
                self.record_usage(decision)
            if generation != self.generation or not self.active:
                fields = {"usage": self.decision_record(decision)["usage"]} if decision is not None else {}
                self.event(
                    "WARN", "Sent AI-svar efter stop blev ignoreret.",
                    kind="replica_late_reply_discarded", **fields,
                )
            elif error:
                self.fail(error)
            else:
                self.accept(decision)
        self.root.after(100, self.poll)

    def submit(self, kind, function):
        self.rpc_pending += 1
        self.rpc_tasks.put((self.generation, kind, function))

    def rpc_worker(self):
        # One queue owns all pipe traffic; a hung target cannot block Tk or STOP.
        while True:
            generation, kind, function = self.rpc_tasks.get()
            if kind == "quit":
                return
            try:
                value, error = function(), None
                if kind == "launch" and self.closed:
                    value.close()
                    value = None
            except Exception as exc:
                value = None
                error = str(exc) if isinstance(exc, (ValueError, RuntimeError, PermissionError)) else "Forespørgslen til testprogrammet fejlede."
            self.rpc_results.put((generation, kind, value, error))

    def accept(self, decision):
        record = self.decision_record(decision)
        self.event(
            "AI", f"{decision.stat_type}={decision.observed_value}; {decision.action}. {decision.reason}",
            kind="replica_decision", **record,
        )
        flow = self.flow
        self.submit("accept", lambda: (flow.accept(decision), decision))

    def record_usage(self, decision):
        usage = decision.usage
        self.input_tokens += usage.input_tokens
        self.output_tokens += usage.output_tokens
        self.total_tokens += usage.total_tokens
        self.update_usage_text()

    @staticmethod
    def decision_record(decision):
        """Return JSON-safe model fields plus provider-supplied accounting."""
        record = decision.model_dict()
        record["usage"] = {
            "provider": decision.usage.provider,
            "input_tokens": decision.usage.input_tokens,
            "output_tokens": decision.usage.output_tokens,
            "total_tokens": decision.usage.total_tokens,
        }
        return record

    def update_usage_text(self):
        limit = getattr(self, "run_call_limit", self.call_limit.get())
        self.usage_text.set(
            f"Forbrug: {self.calls}/{limit} AI-kald/trin | "
            f"{self.input_tokens} inputtokens | {self.output_tokens} outputtokens"
        )

    def after_accept(self):
        if self.flow.phase == "approve":
            proposal = self.flow.approval()
            self.pending = proposal
            self.approval_text.set(f"{proposal['stat']}: skriv {proposal['value']} til {hex(proposal['address'])} i eget testprogram. Én ændring, revision {proposal['revision']}.")
            self.approve_button.configure(state="normal")
            self.reject_button.configure(state="normal")
            self.status.set("Præcis én kandidat er fundet. Afventer din godkendelse; ingen AI-kald imens.")
        elif self.flow.phase == "done":
            self.stop(completed=True)
            self.status.set("Demo gennemført: testdata kontrolleret; slutbilledet er optaget, men ingen billed-AI er afprøvet." if self.run_provider == PROVIDERS[0] else "Forløbet er gennemført: læsning af testdata og efterfølgende AI-billedkontrol bestod.")
        else:
            self.after_id = self.root.after(700, self.next_step)

    def present(self, event):
        kind = event["event"]
        if kind in ("initial_scan", "rescan"):
            self.details.set(f"Valgt værdi: {self.flow.stat_type}\nAflæst: {event['value']}\nKandidater: {event['count']}\n" + "\n".join(event["addresses"][:4]))
            self.event("OK", f"{'Første søgning' if kind == 'initial_scan' else 'Ny søgning'}: {event['count']} kandidater til værdien {event['value']}.", kind=kind, **{k:v for k,v in event.items() if k != "event"})
        elif kind == "test_action":
            self.event("INFO", f"Tilladt handling {event['action']}: {event['before']} -> {event['after']}.", kind=kind, **{k:v for k,v in event.items() if k != "event"})
        else:
            if kind == "visual_verification_passed":
                self.details.set(f"Valgt værdi: {event['stat']}\nSlutværdi: {event['value']}\nKandidater: 1\n" + ("Demo: kontrol mod testdata" if self.run_provider == PROVIDERS[0] else "AI-billedkontrol bestod"))
                message = "Demo: slutkontrol mod testdata bestod. Ingen billedgenkendelse." if self.run_provider == PROVIDERS[0] else "AI aflæste slutværdien korrekt fra et nyt billede."
                self.event("OK", message, kind=kind, provider=self.run_provider, value=event["value"])
            else:
                self.event("OK", "Resultat kontrolleret: " + json.dumps(event, ensure_ascii=False), kind=kind)

    def save_plan(self):
        # Data-only plan replaces executable AHK scripts. Never run this file as code.
        path = data_dir() / "last-plan.json"
        path.write_text(json.dumps({"format": "owned-lab-actions-v1", "actions": self.plan}, indent=2, ensure_ascii=False), encoding="utf-8")

    def approve(self):
        if not self.active or not self.pending:
            return
        self.pending = None
        self.approve_button.configure(state="disabled")
        self.reject_button.configure(state="disabled")
        self.approval_text.set("Godkendelsen er brugt til én testændring.")
        flow = self.flow
        self.submit("write", lambda: flow.write(approved=True))

    def reject(self):
        self.event("WARN", "Brugeren afviste skrivningen.", kind="replica_write_rejected")
        self.stop()

    def stop(self, completed=False):
        was_active = self.active
        self.active = False
        self.generation += 1
        self.pending = None
        self.start_after_launch = False
        if self.flow:
            self.flow.stop()
        if self.after_id is not None:
            self.root.after_cancel(self.after_id)
            self.after_id = None
        if self.client:
            self.client.halt()  # Local send gate closes before returning to Tk.
            if self.client.process.poll() is None and not self.client.failed and not self.client.closed:
                client = self.client
                self.submit("stop", lambda: client.request("stop"))
        self.approve_button.configure(state="disabled")
        self.reject_button.configure(state="disabled")
        self.approval_text.set("Ingen ændring afventer godkendelse.")
        self.settings_enabled(True)
        self.status.set("Stoppet. Ingen nye handlinger; sene AI-svar ignoreres.")
        if was_active:
            self.event(
                "OK" if completed else "WARN", "Forløb afsluttet." if completed else "Agenten er stoppet.",
                kind="replica_completed" if completed else "replica_stopped", calls=self.calls,
                input_tokens=self.input_tokens, output_tokens=self.output_tokens,
                total_tokens=self.total_tokens,
            )

    def fail(self, message):
        self.stop()
        self.status.set(message)
        self.event("ERROR", message, kind="replica_error")

    def reset(self):
        self.stop()
        if self.client and self.client.process.poll() is None:
            client = self.client
            self.submit("reset", lambda: client.request("reset"))

    def close(self):
        self.closed = True
        self.stop()
        if self.client:
            self.client.halt()
            # Terminate only the child we launched. Never wait on the GUI thread.
            if self.client.process.poll() is None:
                self.client.process.terminate()
            self.submit("cleanup", self.client.close)
        self.rpc_tasks.put((self.generation, "quit", None))
        self.root.destroy()
