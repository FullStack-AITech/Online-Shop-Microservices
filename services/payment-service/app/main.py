"""Application entrypoint: builds the FastAPI app and maps domain errors onto HTTP.

The event consumer is a separate process (``python -m app.consumer``) built from the
same image, so a slow broker never holds up the API and each can be scaled on its own.
"""

import logging
from contextlib import asynccontextmanager

from fastapi import FastAPI, Request, status
from fastapi.responses import JSONResponse

from app.api.v1 import health
from app.api.v1.router import api_router
from app.core.config import get_settings
from app.core.logging import configure_logging
from app.exceptions import (
    IdempotencyKeyReusedError,
    IllegalPaymentTransitionError,
    InvalidPaymentError,
    PaymentNotFoundError,
)

logger = logging.getLogger(__name__)

API_V1_PREFIX = "/api/v1"


@asynccontextmanager
async def lifespan(app: FastAPI):
    settings = get_settings()
    configure_logging()
    # No create_all: the schema is Alembic's, applied before the container starts uvicorn.
    logger.info("starting %s in %s", settings.service_name, settings.environment)
    yield
    logger.info("stopping %s", settings.service_name)


def create_app() -> FastAPI:
    settings = get_settings()
    app = FastAPI(
        title="Payment Service",
        version="0.1.0",
        description=(
            "Takes payment for orders on the Online Shop platform, once per idempotency "
            "key. Other services learn the outcome from PaymentProcessed and PaymentFailed "
            "events and never read this service's database."
        ),
        lifespan=lifespan,
        docs_url="/docs",
        openapi_url="/openapi.json",
    )

    app.include_router(health.router)
    app.include_router(api_router, prefix=API_V1_PREFIX)

    @app.exception_handler(PaymentNotFoundError)
    async def _payment_not_found(request: Request, exc: PaymentNotFoundError) -> JSONResponse:
        return _error(status.HTTP_404_NOT_FOUND, "payment_not_found", str(exc))

    @app.exception_handler(IdempotencyKeyReusedError)
    async def _key_reused(request: Request, exc: IdempotencyKeyReusedError) -> JSONResponse:
        return _error(status.HTTP_409_CONFLICT, "idempotency_key_reused", str(exc))

    @app.exception_handler(IllegalPaymentTransitionError)
    async def _illegal_transition(
        request: Request, exc: IllegalPaymentTransitionError
    ) -> JSONResponse:
        return _error(status.HTTP_409_CONFLICT, "illegal_payment_transition", str(exc))

    @app.exception_handler(InvalidPaymentError)
    async def _invalid_payment(request: Request, exc: InvalidPaymentError) -> JSONResponse:
        # The request schema already rejects these; this catches a caller that bypasses it.
        return _error(422, "invalid_payment", str(exc))

    @app.get("/", include_in_schema=False)
    async def root() -> dict[str, str]:
        return {"service": settings.service_name, "docs": "/docs", "api": API_V1_PREFIX}

    return app


def _error(status_code: int, code: str, message: str) -> JSONResponse:
    """Every error response shares this shape so the gateway can handle them uniformly."""
    return JSONResponse(status_code=status_code, content={"error": code, "message": message})


app = create_app()
