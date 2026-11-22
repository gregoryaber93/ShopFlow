namespace ShopFlow.SharedKernel.Domain;

/// <summary>
/// Violation of a domain invariant, i.e. a situation that correctly written calling code should never
/// allow. Expected business refusals are returned through <see cref="Results.Result"/>.
/// </summary>
public class DomainException : Exception
{
    public DomainException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    public DomainException(string code, string message, Exception innerException)
        : base(message, innerException)
    {
        Code = code;
    }

    /// <summary>Stable error code, e.g. <c>order.already_confirmed</c>. Goes to logs and ProblemDetails.</summary>
    public string Code { get; }
}
