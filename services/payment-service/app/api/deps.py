"""Shared FastAPI dependencies — the wiring between HTTP and the domain."""

from typing import Annotated

from fastapi import Depends
from sqlalchemy.orm import Session

from app.core.config import Settings, get_settings
from app.db.session import get_session
from app.providers import PaymentProvider, build_provider
from app.repositories.payment import PaymentRepository
from app.services.payment import PaymentService

SessionDep = Annotated[Session, Depends(get_session)]
SettingsDep = Annotated[Settings, Depends(get_settings)]


def get_payment_provider(settings: SettingsDep) -> PaymentProvider:
    return build_provider(settings)


ProviderDep = Annotated[PaymentProvider, Depends(get_payment_provider)]


def get_payment_service(session: SessionDep, provider: ProviderDep) -> PaymentService:
    return PaymentService(PaymentRepository(session), provider)


PaymentServiceDep = Annotated[PaymentService, Depends(get_payment_service)]
