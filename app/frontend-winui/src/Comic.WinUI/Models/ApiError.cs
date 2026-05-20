using System;
using System.Text.Json.Serialization;

namespace Comic.WinUI.Models;

public sealed class ApiError
{
    [JsonPropertyName("code")]
    public string Code { get; set; } = string.Empty;

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;
}

public sealed class BackendApiException : Exception
{
    public ApiError Error { get; }

    public BackendApiException(ApiError error) : base(error.Message)
    {
        Error = error;
    }
}
