using HomelabMapper.Core.Interfaces;

namespace HomelabMapper.Core.Services;

public class InMemoryCredentialStore : ICredentialStore
{
    private readonly Dictionary<string, Dictionary<string, string>> _credentials = new();

    public string? GetCredential(string service, string key)
    {
        if (_credentials.TryGetValue(service, out var serviceCredentials))
        {
            return serviceCredentials.GetValueOrDefault(key);
        }
        return null;
    }

    public void SetCredential(string service, string key, string value)
    {
        if (!_credentials.ContainsKey(service))
        {
            _credentials[service] = new Dictionary<string, string>();
        }
        _credentials[service][key] = value;
    }
}
