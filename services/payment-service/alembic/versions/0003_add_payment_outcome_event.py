"""add payment outcome event

Revision ID: 0003
Revises: 0002
Create Date: 2026-09-24 10:58:58.329854

"""
from collections.abc import Sequence

import sqlalchemy as sa
from alembic import op


# revision identifiers, used by Alembic.
revision: str = '0003'
down_revision: str | Sequence[str] | None = '0002'
branch_labels: str | Sequence[str] | None = None
depends_on: str | Sequence[str] | None = None


def upgrade() -> None:
    # Both nullable: a payment only gets an outcome event once it reaches Captured or
    # Failed, and rows written before this revision have none.
    with op.batch_alter_table('payments', schema=None) as batch_op:
        batch_op.add_column(sa.Column('outcome_event_id', sa.String(length=36), nullable=True))
        batch_op.add_column(sa.Column('outcome_published_at', sa.DateTime(timezone=True), nullable=True))


def downgrade() -> None:
    with op.batch_alter_table('payments', schema=None) as batch_op:
        batch_op.drop_column('outcome_published_at')
        batch_op.drop_column('outcome_event_id')
