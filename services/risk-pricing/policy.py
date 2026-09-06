"""Transparent baseline rules, not a trained or calibrated fraud model."""
import json
import math
import re
from pathlib import Path

BASE = json.loads(Path(__file__).with_name("pricing-policy.json").read_text())

def score(verified: bool, late: int, completed: int) -> int:
    # Laplace smoothing avoids overconfidence on the first rental.
    late_fraction = (late + 1) / (completed + 2)
    log_odds = -2.0 + 3.0 * late_fraction + (0.0 if verified else 2.0)
    return round(100 / (1 + math.exp(-log_odds)))

def verify_number(text: str, claimed: str) -> bool:
    if not re.fullmatch(r"[0-9]{11}", claimed):
        return False
    # Match a complete digit token; do not turn arbitrary page digits into a CNH.
    return claimed in re.findall(r"(?<![0-9])[0-9]{11}(?![0-9])", text)

def pricing(active: int, capacity: int, now_ms: int) -> dict:
    if capacity <= 0:
        raise ValueError("fleet capacity must be positive")
    # Integer basis points and half-up rounding preserve exact minor units.
    multiplier = 10000 + min(3000, max(0, active) * 2000 // capacity)
    return {**BASE, "version": str(now_ms), "published_at_ms": now_ms,
            "tiers": [{**tier, "daily_minor": (tier["daily_minor"] * multiplier + 5000)//10000}
                      for tier in BASE["tiers"]]}
