"""Application entrypoint: builds the FastAPI app and maps domain errors onto HTTP."""

import logging
from contextlib import asynccontextmanager

from fastapi import FastAPI, Request, status
from fastapi.responses import JSONResponse

from app.api.v1 import health
from app.api.v1.router import api_router
from app.core.config import get_settings
from app.core.logging import configure_logging
from app.db.session import create_tables
from app.exceptions import EmailAlreadyRegisteredError, UserNotFoundError

logger = logging.getLogger(__name__)

API_V1_PREFIX = "/api/v1"


@asynccontextmanager
async def lifespan(app: FastAPI):
    settings = get_settings()
    configure_logging()
    logger.info("starting %s in %s", settings.service_name, settings.environment)
    if settings.create_tables_on_startup:
        create_tables()
    yield
    logger.info("stopping %s", settings.service_name)


def create_app() -> FastAPI:
    settings = get_settings()
    app = FastAPI(
        title="User Service",
        version="0.1.0",
        description=(
            "Owns user accounts and profiles for the Online Shop platform. "
            "Other services reference users by id and never read this service's database."
        ),
        lifespan=lifespan,
        docs_url="/docs",
        openapi_url="/openapi.json",
    )

    app.include_router(health.router)
    app.include_router(api_router, prefix=API_V1_PREFIX)

    @app.exception_handler(UserNotFoundError)
    async def _user_not_found(request: Request, exc: UserNotFoundError) -> JSONResponse:
        return _error(status.HTTP_404_NOT_FOUND, "user_not_found", str(exc))

    @app.exception_handler(EmailAlreadyRegisteredError)
    async def _email_taken(request: Request, exc: EmailAlreadyRegisteredError) -> JSONResponse:
        return _error(status.HTTP_409_CONFLICT, "email_already_registered", str(exc))

    @app.get("/", include_in_schema=False)
    async def root() -> dict[str, str]:
        return {"service": settings.service_name, "docs": "/docs", "api": API_V1_PREFIX}

    return app


def _error(status_code: int, code: str, message: str) -> JSONResponse:
    """Every error response shares this shape so the gateway can handle them uniformly."""
    return JSONResponse(status_code=status_code, content={"error": code, "message": message})


app = create_app()
