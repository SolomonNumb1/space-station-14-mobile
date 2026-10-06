using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Content.Android.Launcher.Models;

namespace Content.Android.Launcher.Api;

public sealed class AuthClient
{
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(12)
    };

    private static readonly string[] AuthBases =
    {
        "https://auth.spacestation14.com/",
        "https://auth.fallback.spacestation14.com/"
    };

    public async Task<AuthResult> AuthenticateAsync(string username, string password, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
            return AuthResult.Fail("Username and password are required.");

        var req = new AuthAuthenticateRequest(username.Trim(), password);

        foreach (var baseUrl in AuthBases)
        {
            try
            {
                var endpoint = baseUrl + "api/auth/authenticate";
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(10));

                using var resp = await Http.PostAsJsonAsync(endpoint, req, cts.Token);

                if (resp.IsSuccessStatusCode)
                {
                    var data = await resp.Content.ReadFromJsonAsync<AuthAuthenticateResponse>(cancellationToken: cts.Token);
                    if (data != null && !string.IsNullOrEmpty(data.Token))
                    {
                        var account = new AccountInfo
                        {
                            UserId = data.UserId.ToString(),
                            Username = data.Username,
                            Token = data.Token,
                            Expires = data.ExpireTime
                        };
                        return AuthResult.Ok(account);
                    }
                }

                if (resp.StatusCode == HttpStatusCode.Unauthorized || resp.StatusCode == HttpStatusCode.BadRequest)
                {
                    try
                    {
                        var err = await resp.Content.ReadFromJsonAsync<AuthErrorResponse>(cancellationToken: cts.Token);
                        if (err?.Errors != null && err.Errors.Length > 0)
                        {
                            return AuthResult.Fail(string.Join("\n", err.Errors));
                        }
                    }
                    catch
                    {
                    }
                    return AuthResult.Fail("Invalid login or password.");
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
            }
        }

        return AuthResult.Fail("Could not connect to authentication servers.");
    }

    public async Task<bool> ValidateTokenAsync(string token, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(token)) return false;

        foreach (var baseUrl in AuthBases)
        {
            try
            {
                var endpoint = baseUrl + "api/auth/ping";
                using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(5));

                using var resp = await Http.SendAsync(request, cts.Token);
                if (resp.IsSuccessStatusCode) return true;
            }
            catch
            {
            }
        }

        return false;
    }

    public async Task LogoutAsync(string token)
    {
        if (string.IsNullOrWhiteSpace(token)) return;

        foreach (var baseUrl in AuthBases)
        {
            try
            {
                var endpoint = baseUrl + "api/auth/logout";
                using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
                request.Content = JsonContent.Create(new { token });
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await Http.SendAsync(request, cts.Token);
                return;
            }
            catch
            {
            }
        }
    }
}
