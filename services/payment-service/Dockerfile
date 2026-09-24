# syntax=docker/dockerfile:1

# Multi-stage build: wheels are compiled once, the runtime image stays small.
FROM python:3.13-slim AS builder

WORKDIR /build
COPY requirements.txt .
RUN pip install --no-cache-dir --upgrade pip \
    && pip wheel --no-cache-dir --wheel-dir /wheels -r requirements.txt


FROM python:3.13-slim AS runtime

ENV PYTHONDONTWRITEBYTECODE=1 \
    PYTHONUNBUFFERED=1

WORKDIR /app

COPY --from=builder /wheels /wheels
COPY requirements.txt .
RUN pip install --no-cache-dir --no-index --find-links=/wheels -r requirements.txt \
    && rm -rf /wheels

COPY app ./app
# The migrations ship in the image so the container can bring its own database up to date.
COPY alembic ./alembic
COPY alembic.ini .

# Run as an unprivileged user; containers should never need root.
RUN useradd --create-home --uid 10001 appuser
USER appuser

EXPOSE 8083

# Probes the API. The consumer runs from this same image with a different command and
# serves no HTTP, so Compose disables this check for that container.
HEALTHCHECK --interval=30s --timeout=3s --start-period=10s --retries=3 \
    CMD python -c "import urllib.request,sys; sys.exit(0 if urllib.request.urlopen('http://localhost:8083/health/live').status == 200 else 1)"

# Migrate, then replace the shell with uvicorn so it receives SIGTERM directly.
CMD ["sh", "-c", "alembic upgrade head && exec uvicorn app.main:app --host 0.0.0.0 --port 8083"]
