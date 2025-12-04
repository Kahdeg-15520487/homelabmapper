using PuppeteerSharp;
using System.Text.RegularExpressions;

namespace HomelabMapper.Integration;

/// <summary>
/// Client for scraping DHCP lease information from a router web UI using PuppeteerSharp.
/// This implementation is designed for routers with web-based management interfaces.
/// </summary>
public class RouterF670YClient : IDisposable
{
    private readonly string _routerUrl;
    private readonly string _username;
    private readonly string _password;
    private IBrowser? _browser;
    private IPage? _page;
    private bool _isLoggedIn;

    public RouterF670YClient(string routerIp, string username, string password)
    {
        _routerUrl = $"http://{routerIp}";
        _username = username;
        _password = password;
    }

    /// <summary>
    /// Initializes the browser and downloads Chromium if needed.
    /// </summary>
    public async Task InitializeAsync()
    {
        // Determine Chrome path: environment variable, default Windows path, or default Linux path
        var chromePath = Environment.GetEnvironmentVariable("CHROME_PATH") 
            ?? (OperatingSystem.IsWindows() 
                ? @"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe" 
                : "/usr/bin/google-chrome-stable");
        
        // Launch browser using installed Chrome
        _browser = await Puppeteer.LaunchAsync(new LaunchOptions
        {
            Headless = true,
            ExecutablePath = chromePath,
            Args = new[] { "--no-sandbox", "--disable-setuid-sandbox", "--ignore-certificate-errors" }
        });

        _page = await _browser.NewPageAsync();
        
        // Ignore SSL certificate errors
        await _page.SetBypassCSPAsync(true);
        
        // Set a reasonable timeout
        _page.DefaultTimeout = 30000;
    }

    /// <summary>
    /// Logs into the router web interface.
    /// </summary>
    public async Task<bool> LoginAsync()
    {
        if (_page == null)
        {
            throw new InvalidOperationException("Browser not initialized. Call InitializeAsync first.");
        }

        try
        {
            // Navigate to router login page
            await _page.GoToAsync(_routerUrl, new NavigationOptions
            {
                WaitUntil = new[] { WaitUntilNavigation.Networkidle0 }
            });

            // Wait for login form to appear
            await _page.WaitForSelectorAsync("#Frm_Username", new WaitForSelectorOptions
            {
                Timeout = 10000
            });

            // Fill username field
            await _page.TypeAsync("#Frm_Username", _username);

            // Fill password field
            await _page.TypeAsync("#Frm_Password", _password);

            // Click login button
            await _page.ClickAsync("#LoginId");

            // Wait for navigation after login
            await Task.Delay(2000);

            _isLoggedIn = true;
            return true;
        }
        catch (Exception)
        {
            _isLoggedIn = false;
            return false;
        }
    }

    /// <summary>
    /// Navigates to DHCP lease table page and extracts lease information.
    /// Returns list of DHCP leases with MAC addresses, IP addresses, and hostnames.
    /// </summary>
    public async Task<List<DhcpLease>> GetDhcpLeasesAsync()
    {
        if (_page == null || !_isLoggedIn)
        {
            throw new InvalidOperationException("Must login first. Call LoginAsync.");
        }

        var leases = new List<DhcpLease>();

        try
        {
            // Switch to Advance mode
            await _page.ClickAsync("#Btn_Close");
            await Task.Delay(2000);

            // Fetch access point devices' ip
            // Navigate to Topology menu
            await _page.ClickAsync("#mmTopology");
            await Task.Delay(2000);
            
            // Click topology form bar to expand
            await _page.ClickAsync("#topoFormBar");
            await Task.Delay(2000);
            
            // Get the number of entries
            var instNumElement = await _page.QuerySelectorAsync("#_InstNum");
            var instNumValue = await _page.EvaluateFunctionAsync<string>("el => el.value", instNumElement);
            var instNum = int.Parse(instNumValue);
            
            // Parse each device entry
            for (int i = 0; i < instNum; i++)
            {
                var macElement = await _page.QuerySelectorAsync($"#APMAC_{i}");
                var ipElement = await _page.QuerySelectorAsync($"#APIP_{i}");
                var nameElement = await _page.QuerySelectorAsync($"#devideName_{i}");
                var roleElement = await _page.QuerySelectorAsync($"#role_{i}");
                var backhaulElement = await _page.QuerySelectorAsync($"#returnHz_{i}");
                
                if (macElement != null && ipElement != null)
                {
                    var mac = await _page.EvaluateFunctionAsync<string>("el => el.getAttribute('title')", macElement);
                    var ip = await _page.EvaluateFunctionAsync<string>("el => el.getAttribute('title')", ipElement);
                    var name = nameElement != null 
                        ? await _page.EvaluateFunctionAsync<string>("el => el.getAttribute('title')", nameElement)
                        : null;
                    var role = roleElement != null
                        ? await _page.EvaluateFunctionAsync<string>("el => el.getAttribute('title')", roleElement)
                        : null;
                    var backhaul = backhaulElement != null
                        ? await _page.EvaluateFunctionAsync<string>("el => el.getAttribute('title')", backhaulElement)
                        : null;
                    
                    leases.Add(new DhcpLease
                    {
                        MacAddress = mac?.Replace('-', ':').ToUpper(),
                        IpAddress = ip,
                        Hostname = name,
                        IsAccessPoint = true,
                        Role = role,
                        Backhaul = backhaul
                    });
                }
            }

            // Navigate to Local network menu
            await _page.ClickAsync("#localnet");
            await Task.Delay(2000);
            
            // Click lan devices bar to expand
            await _page.ClickAsync("#LANDevsBar");
            await Task.Delay(2000);
            
            // Parse LAN devices - find all template_LANDevs_* divs
            var lanDeviceIndex = 0;
            while (true)
            {
                var deviceDiv = await _page.QuerySelectorAsync($"#template_LANDevs_{lanDeviceIndex}");
                if (deviceDiv == null)
                    break;
                
                // Check if the div is visible (not display:none)
                var isVisible = await _page.EvaluateFunctionAsync<bool>(
                    "el => el.style.display !== 'none'", deviceDiv);
                
                if (isVisible)
                {
                    var macElement = await _page.QuerySelectorAsync($"#MACAddress\\:LANDevs\\:{lanDeviceIndex}");
                    var ipElement = await _page.QuerySelectorAsync($"#IPAddress\\:LANDevs\\:{lanDeviceIndex}");
                    var nameElement = await _page.QuerySelectorAsync($"#HostName\\:LANDevs\\:{lanDeviceIndex}");
                    
                    if (macElement != null && ipElement != null)
                    {
                        var mac = await _page.EvaluateFunctionAsync<string>("el => el.getAttribute('title')", macElement);
                        var ip = await _page.EvaluateFunctionAsync<string>("el => el.getAttribute('title')", ipElement);
                        var name = nameElement != null 
                            ? await _page.EvaluateFunctionAsync<string>("el => el.getAttribute('title')", nameElement)
                            : null;
                        
                        // Only add if not already in the list (avoid duplicates from topology section)
                        if (!string.IsNullOrEmpty(mac) && !string.IsNullOrEmpty(ip))
                        {
                            var normalizedMac = mac.Replace('-', ':').ToUpper();
                            if (!leases.Any(l => l.MacAddress == normalizedMac && l.IpAddress == ip))
                            {
                                leases.Add(new DhcpLease
                                {
                                    MacAddress = normalizedMac,
                                    IpAddress = ip,
                                    Hostname = string.IsNullOrWhiteSpace(name) ? null : name
                                });
                            }
                        }
                    }
                }
                
                lanDeviceIndex++;
            }
        }
        catch (Exception)
        {
            // Return whatever we managed to parse
        }

        return leases;
    }

    /// <summary>
    /// Takes a screenshot of the current page for debugging.
    /// </summary>
    public async Task<byte[]> TakeScreenshotAsync()
    {
        if (_page == null)
        {
            throw new InvalidOperationException("Browser not initialized.");
        }

        return await _page.ScreenshotDataAsync();
    }

    public void Dispose()
    {
        _page?.CloseAsync().Wait();
        _browser?.CloseAsync().Wait();
        _browser?.Dispose();
    }
}

/// <summary>
/// Represents a DHCP lease entry from the router.
/// </summary>
public class DhcpLease
{
    public string? MacAddress { get; set; }
    public string? IpAddress { get; set; }
    public string? Hostname { get; set; }
    public DateTime? LeaseExpiry { get; set; }
    public bool IsAccessPoint { get; set; }
    public string? Role { get; set; }
    public string? Backhaul { get; set; }

    public override string ToString()
    {
        return $"MAC: {MacAddress ?? "N/A"}, IP: {IpAddress ?? "N/A"}, Hostname: {Hostname ?? "N/A"}";
    }
}
