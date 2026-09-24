using System.Globalization;

namespace OrderService.Application.Events;

/// <summary>Money on the wire: a decimal string with two places, never a JSON number.</summary>
/// <remarks>
/// A JSON number becomes a <c>float</c> in the Python consumer, and a float is not a price.
/// Every stack in the platform parses a string losslessly.
/// </remarks>
internal static class Money
{
    public static string Format(decimal amount) =>
        amount.ToString("0.00", CultureInfo.InvariantCulture);
}
