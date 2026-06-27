// Copyright (c) marcschier. Licensed under the MIT License.

namespace NanoMsg.Transports;

/// <summary>
/// A parsed nanomsg endpoint address of the form <c>scheme://detail</c>. Supported schemes are
/// <c>inproc</c>, <c>tcp</c>, <c>ipc</c>, and <c>ws</c>. The same value type is used for both
/// <c>bind</c> and <c>connect</c>; a TCP host of <c>*</c> denotes a bind wildcard.
/// </summary>
internal readonly struct NanoAddress
{
    private NanoAddress(
        AddressScheme scheme,
        string original,
        string host,
        int port,
        string path,
        string? localInterface,
        AddressFamilyPreference familyPreference = AddressFamilyPreference.Unspecified)
    {
        Scheme = scheme;
        Original = original;
        Host = host;
        Port = port;
        Path = path;
        LocalInterface = localInterface;
        FamilyPreference = familyPreference;
    }

    /// <summary>Gets the transport scheme.</summary>
    public AddressScheme Scheme { get; }

    /// <summary>Gets the original, unparsed address string.</summary>
    public string Original { get; }

    /// <summary>
    /// Gets the host for <c>tcp</c>/<c>ws</c> addresses (<c>*</c> for a bind wildcard); otherwise empty.
    /// </summary>
    public string Host { get; }

    /// <summary>Gets the port for <c>tcp</c>/<c>ws</c> addresses; otherwise 0.</summary>
    public int Port { get; }

    /// <summary>
    /// Gets the path: the <c>ipc</c> filesystem/pipe path, the <c>inproc</c> name, or the <c>ws</c> resource.
    /// </summary>
    public string Path { get; }

    /// <summary>Gets the optional local source interface for a <c>tcp</c> address (the part before <c>;</c>).</summary>
    public string? LocalInterface { get; }

    /// <summary>Gets the IPv4/IPv6 restriction parsed from a scheme suffix (<c>tcp4</c>/<c>tcp6</c>, etc.).</summary>
    public AddressFamilyPreference FamilyPreference { get; }

    /// <summary>Gets a value indicating whether the host is the bind wildcard <c>*</c>.</summary>
    public bool IsWildcardHost => Host == "*";

    /// <summary>Parses <paramref name="address"/>, throwing on any malformed input.</summary>
    /// <param name="address">The address string, for example <c>tcp://127.0.0.1:5555</c>.</param>
    /// <returns>The parsed <see cref="NanoAddress"/>.</returns>
    /// <exception cref="NanoMsgException">The address is null, empty, or malformed.</exception>
    public static NanoAddress Parse(string address)
    {
        if (!TryParse(address, out NanoAddress result, out string? error))
        {
            throw new NanoMsgException($"Invalid endpoint address '{address}': {error}");
        }

        return result;
    }

    /// <summary>Attempts to parse <paramref name="address"/>.</summary>
    /// <param name="address">The address string.</param>
    /// <param name="result">When this method returns <see langword="true"/>, the parsed address.</param>
    /// <returns><see langword="true"/> if parsing succeeded.</returns>
    public static bool TryParse(string address, out NanoAddress result) =>
        TryParse(address, out result, out _);

    private static bool TryParse(string address, out NanoAddress result, out string? error)
    {
        result = default;
        error = null;

        if (string.IsNullOrEmpty(address))
        {
            error = "the address is empty";
            return false;
        }

        int sep = address.IndexOf("://", StringComparison.Ordinal);
        if (sep <= 0)
        {
            error = "missing '://' scheme separator";
            return false;
        }

        string schemeText = address.Substring(0, sep).ToLowerInvariant();
        string detail = address.Substring(sep + 3);
        if (detail.Length == 0)
        {
            error = "missing address detail after '://'";
            return false;
        }

        AddressFamilyPreference family = StripFamilySuffix(ref schemeText);

        switch (schemeText)
        {
            case "inproc":
                result = new NanoAddress(AddressScheme.InProc, address, string.Empty, 0, detail, null);
                return true;

            case "ipc":
                result = new NanoAddress(AddressScheme.Ipc, address, string.Empty, 0, detail, null);
                return true;

            case "tcp":
                return TryParseTcp(address, detail, AddressScheme.Tcp, family, out result, out error);

            case "tls+tcp":
                return TryParseTcp(address, detail, AddressScheme.TlsTcp, family, out result, out error);

            case "ws":
                return TryParseWs(address, detail, AddressScheme.Ws, family, out result, out error);

            case "wss":
                return TryParseWs(address, detail, AddressScheme.Wss, family, out result, out error);

            case "udp":
                return TryParseTcp(address, detail, AddressScheme.Udp, family, out result, out error);

            case "dtls+udp":
                return TryParseTcp(address, detail, AddressScheme.DtlsUdp, family, out result, out error);

            default:
                error = $"unsupported scheme '{schemeText}'";
                return false;
        }
    }

    private static AddressFamilyPreference StripFamilySuffix(ref string scheme)
    {
        char last = scheme.Length == 0 ? '\0' : scheme[scheme.Length - 1];
        if (last != '4' && last != '6')
        {
            return AddressFamilyPreference.Unspecified;
        }

        string baseScheme = scheme.Substring(0, scheme.Length - 1);
        if (baseScheme is "tcp" or "tls+tcp" or "ws" or "wss" or "udp" or "dtls+udp")
        {
            scheme = baseScheme;
            return last == '4' ? AddressFamilyPreference.IPv4 : AddressFamilyPreference.IPv6;
        }

        return AddressFamilyPreference.Unspecified;
    }

    private static bool TryParseTcp(
        string original,
        string detail,
        AddressScheme scheme,
        AddressFamilyPreference family,
        out NanoAddress result,
        out string? error)
    {
        result = default;
        string? localInterface = null;

        int semicolon = detail.IndexOf(';');
        if (semicolon >= 0)
        {
            localInterface = detail.Substring(0, semicolon);
            detail = detail.Substring(semicolon + 1);
        }

        if (!TryParseHostPort(detail, out string host, out int port, out error))
        {
            return false;
        }

        result = new NanoAddress(scheme, original, host, port, string.Empty, localInterface, family);
        return true;
    }

    private static bool TryParseWs(
        string original,
        string detail,
        AddressScheme scheme,
        AddressFamilyPreference family,
        out NanoAddress result,
        out string? error)
    {
        result = default;

        string path = "/";
        int slash = detail.IndexOf('/');
        if (slash >= 0)
        {
            path = detail.Substring(slash);
            detail = detail.Substring(0, slash);
        }

        if (!TryParseHostPort(detail, out string host, out int port, out error))
        {
            return false;
        }

        result = new NanoAddress(scheme, original, host, port, path, null, family);
        return true;
    }

    private static bool TryParseHostPort(string text, out string host, out int port, out string? error)
    {
        host = string.Empty;
        port = 0;
        error = null;

        string portText;
        if (text.StartsWith('['))
        {
            int close = text.IndexOf(']');
            if (close < 0)
            {
                error = "unterminated IPv6 literal";
                return false;
            }

            host = text.Substring(1, close - 1);
            if (close + 1 >= text.Length || text[close + 1] != ':')
            {
                error = "missing ':port' after IPv6 literal";
                return false;
            }

            portText = text.Substring(close + 2);
        }
        else
        {
            int colon = text.LastIndexOf(':');
            if (colon <= 0)
            {
                error = "expected 'host:port'";
                return false;
            }

            host = text.Substring(0, colon);
            portText = text.Substring(colon + 1);
        }

        if (host.Length == 0)
        {
            error = "missing host";
            return false;
        }

        if (!int.TryParse(portText, out port) || port < 0 || port > 65535)
        {
            error = $"invalid port '{portText}'";
            return false;
        }

        return true;
    }
}
