"""Pure, testable action boundary. No desktop control or process memory access."""
from dataclasses import dataclass
import json
import threading


SCHEMA = {
    "type": "object", "additionalProperties": False,
    "properties": {
        "observed_value": {"type": "integer", "minimum": 0, "maximum": 100},
        "action": {"type": "string", "enum": ["add", "set", "finish"]},
        "amount": {"type": "integer", "minimum": -100, "maximum": 100},
        "reason": {"type": "string"},
    },
    "required": ["observed_value", "action", "amount", "reason"],
}


@dataclass(frozen=True)
class Decision:
    observed_value: int
    action: str
    amount: int
    reason: str

    @classmethod
    def parse(cls, raw):
        obj = json.loads(raw)
        if not isinstance(obj, dict) or set(obj) != set(SCHEMA["required"]):
            raise ValueError("AI-svaret har ukendte eller manglende felter")
        if any(type(obj[k]) is not int for k in ("observed_value", "amount")):
            raise ValueError("Værdier skal være heltal")
        if not 0 <= obj["observed_value"] <= 100:
            raise ValueError("Aflæst værdi uden for 0–100")
        if obj["action"] not in ("add", "set", "finish"):
            raise ValueError("Ukendt handling")
        if not isinstance(obj["reason"], str) or not 1 <= len(obj["reason"]) <= 1000:
            raise ValueError("Ugyldig begrundelse")
        a, n = obj["action"], obj["amount"]
        if (a == "add" and (n == 0 or abs(n) > 10)) or (a == "set" and not 0 <= n <= 100) or (a == "finish" and n != 0):
            raise ValueError("Handlingen overskrider de tilladte grænser")
        return cls(**obj)


class TestLab:
    """Only the value owned by this app can be changed by an agent."""
    def __init__(self, value=20):
        self.value = value
        self.revision = 0
        self._lock = threading.RLock()
        self.stopped = threading.Event()

    def reset(self, value):
        if type(value) is not int or not 0 <= value <= 100:
            raise ValueError("Værdi skal være et heltal fra 0 til 100")
        with self._lock:
            self.value = value
            self.revision += 1

    def stop(self):
        with self._lock:
            self.stopped.set()

    def apply(self, decision, revision, target, approved=False):
        # Revalidate even callers that did not use the JSON parser.
        decision = Decision.parse(json.dumps(decision.__dict__))
        with self._lock:
            if self.stopped.is_set():
                raise ValueError("Agenten er stoppet")
            if revision != self.revision or decision.observed_value != self.value:
                raise ValueError("Billedet er forældet, eller AI aflæste en forkert værdi")
            if decision.action == "finish":
                if self.value != target:
                    raise ValueError("AI erklærede målet nået for tidligt")
                return {"before": self.value, "after": self.value, "goal_reached": True}
            if decision.action == "set" and not approved:
                raise PermissionError("Direkte ændring kræver brugerens godkendelse")
            before = self.value
            expected = before + decision.amount if decision.action == "add" else decision.amount
            if not 0 <= expected <= 100:
                raise ValueError("Resultatet ville være uden for 0–100")
            self.value = expected
            self.revision += 1
            if self.value != expected:
                raise RuntimeError("Resultatkontrol fejlede")
            return {"before": before, "after": self.value, "goal_reached": self.value == target}
