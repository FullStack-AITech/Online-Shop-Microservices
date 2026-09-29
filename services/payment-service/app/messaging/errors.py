"""Why a message could not be handled, as far as the retry loop cares.

The split is the one that matters to a consumer: a transient failure (database blip,
broker hiccup) may succeed on the next attempt, a malformed or unknown-version message
never will, so retrying it only blocks the partition for longer.
"""


class NonRetryableError(Exception):
    """Dead-letter immediately; another attempt cannot succeed."""


class MalformedEventError(NonRetryableError):
    """Not JSON, not an envelope, or a payload missing fields this service needs."""


class UnsupportedEventVersionError(NonRetryableError):
    def __init__(self, event_type: str, event_version: int) -> None:
        super().__init__(f"{event_type} version {event_version} is not supported")
        self.event_type = event_type
        self.event_version = event_version
