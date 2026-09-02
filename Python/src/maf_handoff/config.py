from __future__ import annotations

import os
from dataclasses import dataclass
from typing import Literal

from dotenv import load_dotenv

CredentialMode = Literal["cli", "managed_identity"]


class ConfigurationError(ValueError):
    """Raised when required runtime configuration is missing or invalid."""


def _positive_int(name: str, default: int) -> int:
    raw = os.getenv(name, str(default))
    try:
        value = int(raw)
    except ValueError as exc:
        raise ConfigurationError(f"{name} must be an integer") from exc
    if value < 1:
        raise ConfigurationError(f"{name} must be greater than zero")
    return value


@dataclass(frozen=True, slots=True)
class Settings:
    foundry_project_endpoint: str
    foundry_model: str
    credential_mode: CredentialMode = "cli"
    managed_identity_client_id: str | None = None
    max_total_messages: int = 40
    max_agent_turns: int = 6
    log_level: str = "INFO"

    @classmethod
    def from_env(cls, *, require_cloud: bool = True) -> Settings:
        load_dotenv()
        endpoint = os.getenv("FOUNDRY_PROJECT_ENDPOINT", "").strip()
        model = os.getenv("FOUNDRY_MODEL", "").strip()
        mode = os.getenv("AZURE_CREDENTIAL", "cli").strip().lower()

        if mode not in {"cli", "managed_identity"}:
            raise ConfigurationError("AZURE_CREDENTIAL must be 'cli' or 'managed_identity'")
        if require_cloud and not endpoint:
            raise ConfigurationError("FOUNDRY_PROJECT_ENDPOINT is required")
        if require_cloud and not model:
            raise ConfigurationError("FOUNDRY_MODEL is required")

        return cls(
            foundry_project_endpoint=endpoint,
            foundry_model=model,
            credential_mode=mode,  # type: ignore[arg-type]
            managed_identity_client_id=os.getenv("AZURE_CLIENT_ID") or None,
            max_total_messages=_positive_int("MAF_MAX_TOTAL_MESSAGES", 40),
            max_agent_turns=_positive_int("MAF_MAX_AGENT_TURNS", 6),
            log_level=os.getenv("MAF_LOG_LEVEL", "INFO").upper(),
        )

