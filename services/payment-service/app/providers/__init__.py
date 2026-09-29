from app.core.config import Settings
from app.providers.base import PaymentProvider, ProviderResult
from app.providers.fake import FakePaymentProvider


def build_provider(settings: Settings) -> PaymentProvider:
    """Select the provider named by ``PAYMENT_SERVICE_PAYMENT_PROVIDER``."""
    # Settings restricts payment_provider to the names handled here, so an unknown one
    # fails when the settings load rather than at the first charge.
    return FakePaymentProvider(decline_above=settings.fake_decline_above)


__all__ = ["FakePaymentProvider", "PaymentProvider", "ProviderResult", "build_provider"]
