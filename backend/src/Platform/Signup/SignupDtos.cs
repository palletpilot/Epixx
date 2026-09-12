using System.Text.Json.Serialization;

namespace Lagerkraft.Platform.Signup;

public sealed record SignupRequestBody(
    [property: JsonPropertyName("company_name")] string CompanyName,
    [property: JsonPropertyName("org_number")] string OrgNumber,
    [property: JsonPropertyName("email")] string Email,
    [property: JsonPropertyName("display_name")] string DisplayName,
    [property: JsonPropertyName("password")] string Password,
    [property: JsonPropertyName("slug")] string? Slug,
    [property: JsonPropertyName("join_request")] bool JoinRequest);

public sealed record SignupAcceptedResponse(
    [property: JsonPropertyName("signup_id")] Guid SignupId,
    [property: JsonPropertyName("slug")] string Slug);

public sealed record JoinRequestResponse(
    [property: JsonPropertyName("signup_id")] Guid SignupId,
    [property: JsonPropertyName("company_name")] string CompanyName);

public sealed record VerifySignupRequest(
    [property: JsonPropertyName("token")] string Token);

public sealed record ApproveJoinRequest(
    [property: JsonPropertyName("signup_id")] Guid SignupId);
