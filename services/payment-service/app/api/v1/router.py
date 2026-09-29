"""Aggregates every v1 route behind a single prefix."""

from fastapi import APIRouter

from app.api.v1 import payments

api_router = APIRouter()
api_router.include_router(payments.router)
