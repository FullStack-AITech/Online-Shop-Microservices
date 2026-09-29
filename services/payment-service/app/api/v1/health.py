"""Liveness and readiness probes.

Kubernetes needs these to be separate: liveness must not fail because a dependency is
down (that would restart a healthy pod), readiness must fail so traffic is routed away.
"""

from fastapi import APIRouter, status
from fastapi.responses import JSONResponse
from sqlalchemy import text

from app.api.deps import SessionDep, SettingsDep

router = APIRouter(tags=["health"])


@router.get("/health/live", summary="Liveness probe")
def live(settings: SettingsDep) -> dict[str, str]:
    return {"status": "ok", "service": settings.service_name}


@router.get("/health/ready", summary="Readiness probe")
def ready(session: SessionDep, settings: SettingsDep) -> JSONResponse:
    try:
        session.execute(text("SELECT 1"))
    except Exception:  # noqa: BLE001 - any failure means "not ready for traffic"
        return JSONResponse(
            status_code=status.HTTP_503_SERVICE_UNAVAILABLE,
            content={
                "status": "unavailable",
                "service": settings.service_name,
                "database": "unreachable",
            },
        )
    return JSONResponse(
        status_code=status.HTTP_200_OK,
        content={"status": "ok", "service": settings.service_name, "database": "reachable"},
    )
