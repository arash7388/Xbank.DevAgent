import pytest

from maf_handoff.config import ConfigurationError, Settings


def test_settings_require_cloud_values(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.delenv("FOUNDRY_PROJECT_ENDPOINT", raising=False)
    monkeypatch.delenv("FOUNDRY_MODEL", raising=False)

    with pytest.raises(ConfigurationError, match="FOUNDRY_PROJECT_ENDPOINT"):
        Settings.from_env()


def test_settings_reject_invalid_turn_limit(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setenv("MAF_MAX_AGENT_TURNS", "0")

    with pytest.raises(ConfigurationError, match="greater than zero"):
        Settings.from_env(require_cloud=False)
