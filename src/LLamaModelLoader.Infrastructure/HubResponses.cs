using System.Net;

namespace LLamaModelLoader.Infrastructure;

internal static class HubResponses
{
    // Hugging Face answers 401 rather than 404 for a repository that is missing or private, so both get the same message.
    public static void ThrowIfRefused(HttpResponseMessage response)
    {
        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
            throw new HttpRequestException("Hugging Face refused the request (rate limit). Try again later.", null, response.StatusCode);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.NotFound)
            throw new HttpRequestException("The file or repository was not found or is not public.", null, response.StatusCode);
    }
}
