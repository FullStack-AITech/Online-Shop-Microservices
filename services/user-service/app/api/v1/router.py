"""Aggregates every v1 route behind a single prefix."""

from fastapi import APIRouter

from app.api.v1 import users

api_router = APIRouter()
api_router.include_router(users.router)
