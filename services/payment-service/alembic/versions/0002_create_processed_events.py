"""create processed_events

Revision ID: 0002
Revises: 0001
Create Date: 2026-09-24 10:58:37.347126

"""
from collections.abc import Sequence

import sqlalchemy as sa
from alembic import op


# revision identifiers, used by Alembic.
revision: str = '0002'
down_revision: str | Sequence[str] | None = '0001'
branch_labels: str | Sequence[str] | None = None
depends_on: str | Sequence[str] | None = None


def upgrade() -> None:
    # The DDL from "Consuming events" in docs/events/README.md. The composite primary key
    # is what try_record's ON CONFLICT DO NOTHING targets; sa.Uuid is a native UUID on
    # Postgres and CHAR(32) on SQLite.
    op.create_table('processed_events',
    sa.Column('event_id', sa.Uuid(), nullable=False),
    sa.Column('consumer', sa.String(length=64), nullable=False),
    sa.Column('processed_at', sa.DateTime(timezone=True), server_default=sa.func.now(), nullable=False),
    sa.PrimaryKeyConstraint('event_id', 'consumer', name=op.f('pk_processed_events'))
    )


def downgrade() -> None:
    op.drop_table('processed_events')
