using System.Text.RegularExpressions;

namespace MTRTracer.Core.Geo;

/// <summary>
/// Router names often encode their location (IATA codes, city names, carrier-specific abbreviations):
/// "be3061.ccr41.ams03.atlas.cogentco.com", "ae-1.r20.frnkge13.de.bb.gin.ntt.net", "xe-0.waw-bb1-bb1".
/// This table maps such tokens to coordinates. Matches are approximate by nature.
/// </summary>
public static partial class HostnameHints
{
    private sealed record Place(string City, string Cc, double Lat, double Lon);

    // City definitions: name, country, lat, lon, tokens (IATA / metro codes / carrier abbreviations / names).
    private static readonly (string City, string Cc, double Lat, double Lon, string Tokens)[] Cities =
    [
        ("Москва", "RU", 55.7558, 37.6173, "mow msk moscow moskva svo dme vko mskix"),
        ("Санкт-Петербург", "RU", 59.9343, 30.3351, "led spb stpetersburg petersburg piter saintpetersburg"),
        ("Екатеринбург", "RU", 56.8389, 60.6057, "svx ekb ekt yekaterinburg ekaterinburg"),
        ("Новосибирск", "RU", 55.0084, 82.9357, "ovb nsk novosibirsk nsc"),
        ("Казань", "RU", 55.7963, 49.1088, "kzn kazan"),
        ("Нижний Новгород", "RU", 56.3269, 44.0059, "goj nnov nnovgorod nizhny"),
        ("Самара", "RU", 53.1959, 50.1002, "kuf samara smr"),
        ("Ростов-на-Дону", "RU", 47.2357, 39.7015, "rov rnd rostov"),
        ("Краснодар", "RU", 45.0355, 38.9753, "krr krd krasnodar"),
        ("Воронеж", "RU", 51.6720, 39.1843, "voz vrn voronezh"),
        ("Челябинск", "RU", 55.1644, 61.4368, "cek chel chelyabinsk"),
        ("Пермь", "RU", 58.0105, 56.2502, "pee perm"),
        ("Уфа", "RU", 54.7388, 55.9721, "ufa"),
        ("Омск", "RU", 54.9885, 73.3242, "oms omsk"),
        ("Красноярск", "RU", 56.0153, 92.8932, "kja krsk krasnoyarsk"),
        ("Иркутск", "RU", 52.2870, 104.3050, "ikt irkutsk"),
        ("Хабаровск", "RU", 48.4827, 135.0838, "khv khabarovsk"),
        ("Владивосток", "RU", 43.1155, 131.8855, "vvo vladivostok vlad"),
        ("Калининград", "RU", 54.7104, 20.4522, "kgd kaliningrad"),
        ("Минск", "BY", 53.9006, 27.5590, "msq minsk"),
        ("Киев", "UA", 50.4501, 30.5234, "iev kbp kiev kyiv"),
        ("Алматы", "KZ", 43.2220, 76.8512, "ala almaty"),
        ("Астана", "KZ", 51.1694, 71.4491, "nqz astana"),
        ("Ташкент", "UZ", 41.2995, 69.2401, "tas tashkent"),
        ("Тбилиси", "GE", 41.7151, 44.8271, "tbs tbilisi"),
        ("Ереван", "AM", 40.1792, 44.4991, "evn yerevan"),
        ("Баку", "AZ", 40.4093, 49.8671, "gyd baku"),
        ("Рига", "LV", 56.9496, 24.1052, "rix riga"),
        ("Вильнюс", "LT", 54.6872, 25.2797, "vno vilnius"),
        ("Таллин", "EE", 59.4370, 24.7536, "tll tallinn"),
        ("Хельсинки", "FI", 60.1699, 24.9384, "hel helsinki hlsnk"),
        ("Стокгольм", "SE", 59.3293, 18.0686, "sto arn stockholm stk sthlm"),
        ("Осло", "NO", 59.9139, 10.7522, "osl oslo"),
        ("Копенгаген", "DK", 55.6761, 12.5683, "cph copenhagen kbh"),
        ("Варшава", "PL", 52.2297, 21.0122, "waw warsaw wrsw"),
        ("Прага", "CZ", 50.0755, 14.4378, "prg prague praha"),
        ("Вена", "AT", 48.2082, 16.3738, "vie vienna wien"),
        ("Будапешт", "HU", 47.4979, 19.0402, "bud budapest"),
        ("Братислава", "SK", 48.1486, 17.1077, "bts bratislava"),
        ("Бухарест", "RO", 44.4268, 26.1025, "otp buh bucharest"),
        ("София", "BG", 42.6977, 23.3219, "sof sofia"),
        ("Белград", "RS", 44.7866, 20.4489, "beg belgrade"),
        ("Афины", "GR", 37.9838, 23.7275, "ath athens"),
        ("Стамбул", "TR", 41.0082, 28.9784, "ist istanbul"),
        ("Франкфурт", "DE", 50.1109, 8.6821, "fra frankfurt ffm frnkge fft decix"),
        ("Берлин", "DE", 52.5200, 13.4050, "ber txl berlin brln"),
        ("Мюнхен", "DE", 48.1351, 11.5820, "muc munich muenchen mnchen"),
        ("Гамбург", "DE", 53.5511, 9.9937, "ham hamburg"),
        ("Дюссельдорф", "DE", 51.2277, 6.7735, "dus duesseldorf dusseldorf"),
        ("Амстердам", "NL", 52.3676, 4.9041, "ams amsterdam amstnl adam amsix"),
        ("Роттердам", "NL", 51.9244, 4.4777, "rtm rotterdam"),
        ("Брюссель", "BE", 50.8503, 4.3517, "bru brussels"),
        ("Люксембург", "LU", 49.6116, 6.1319, "lux luxembourg"),
        ("Париж", "FR", 48.8566, 2.3522, "par cdg paris prs parigi"),
        ("Марсель", "FR", 43.2965, 5.3698, "mrs marseille"),
        ("Лондон", "GB", 51.5074, -0.1278, "lon lhr ldn london lndn lnd linx"),
        ("Манчестер", "GB", 53.4808, -2.2426, "manchester mnc"),
        ("Дублин", "IE", 53.3498, -6.2603, "dub dublin"),
        ("Мадрид", "ES", 40.4168, -3.7038, "mad madrid"),
        ("Барселона", "ES", 41.3851, 2.1734, "bcn barcelona"),
        ("Лиссабон", "PT", 38.7223, -9.1393, "lis lisbon"),
        ("Милан", "IT", 45.4642, 9.1900, "mil mxp lin milan milano"),
        ("Рим", "IT", 41.9028, 12.4964, "rom fco rome roma"),
        ("Цюрих", "CH", 47.3769, 8.5417, "zrh zurich"),
        ("Женева", "CH", 46.2044, 6.1432, "gva geneva"),
        ("Нью-Йорк", "US", 40.7128, -74.0060, "nyc jfk ewr lga newyork nyk"),
        ("Эшберн", "US", 39.0438, -77.4874, "iad ash ashburn"),
        ("Вашингтон", "US", 38.9072, -77.0369, "wdc dca washington"),
        ("Бостон", "US", 42.3601, -71.0589, "bos boston"),
        ("Чикаго", "US", 41.8781, -87.6298, "chi ord chicago chcgil"),
        ("Даллас", "US", 32.7767, -96.7970, "dal dfw dallas dllstx"),
        ("Хьюстон", "US", 29.7604, -95.3698, "hou iah houston"),
        ("Атланта", "US", 33.7490, -84.3880, "atl atlanta"),
        ("Майами", "US", 25.7617, -80.1918, "mia miami"),
        ("Денвер", "US", 39.7392, -104.9903, "denver"),
        ("Сиэтл", "US", 47.6062, -122.3321, "sea seattle sttlwa"),
        ("Сан-Хосе", "US", 37.3382, -121.8863, "sjc sanjose snjsca"),
        ("Сан-Франциско", "US", 37.7749, -122.4194, "sfo sanfrancisco"),
        ("Пало-Альто", "US", 37.4419, -122.1430, "pao paloalto"),
        ("Лос-Анджелес", "US", 34.0522, -118.2437, "lax losangeles lsanca"),
        ("Феникс", "US", 33.4484, -112.0740, "phx phoenix"),
        ("Торонто", "CA", 43.6532, -79.3832, "yyz tor toronto"),
        ("Монреаль", "CA", 45.5017, -73.5673, "yul montreal"),
        ("Ванкувер", "CA", 49.2827, -123.1207, "yvr vancouver"),
        ("Сан-Паулу", "BR", -23.5505, -46.6333, "gru sao saopaulo"),
        ("Буэнос-Айрес", "AR", -34.6037, -58.3816, "eze buenosaires"),
        ("Сантьяго", "CL", -33.4489, -70.6693, "scl santiago"),
        ("Мехико", "MX", 19.4326, -99.1332, "mex mexico"),
        ("Сингапур", "SG", 1.3521, 103.8198, "sin singapore sgp"),
        ("Гонконг", "HK", 22.3193, 114.1694, "hkg hongkong"),
        ("Токио", "JP", 35.6762, 139.6503, "tyo nrt hnd tokyo tky"),
        ("Осака", "JP", 34.6937, 135.5023, "osa kix osaka"),
        ("Сеул", "KR", 37.5665, 126.9780, "sel icn seoul"),
        ("Тайбэй", "TW", 25.0330, 121.5654, "tpe taipei"),
        ("Шанхай", "CN", 31.2304, 121.4737, "sha pvg shanghai"),
        ("Пекин", "CN", 39.9042, 116.4074, "pek bjs beijing"),
        ("Гуанчжоу", "CN", 23.1291, 113.2644, "guangzhou"),
        ("Мумбаи", "IN", 19.0760, 72.8777, "bom mumbai"),
        ("Ченнаи", "IN", 13.0827, 80.2707, "maa chennai"),
        ("Дели", "IN", 28.7041, 77.1025, "del delhi"),
        ("Дубай", "AE", 25.2048, 55.2708, "dxb dubai"),
        ("Фуджейра", "AE", 25.1288, 56.3265, "fjr fujairah"),
        ("Тель-Авив", "IL", 32.0853, 34.7818, "tlv telaviv"),
        ("Йоханнесбург", "ZA", -26.2041, 28.0473, "jnb johannesburg"),
        ("Кейптаун", "ZA", -33.9249, 18.4241, "cpt capetown"),
        ("Каир", "EG", 30.0444, 31.2357, "cai cairo"),
        ("Сидней", "AU", -33.8688, 151.2093, "syd sydney"),
        ("Мельбурн", "AU", -37.8136, 144.9631, "mel melbourne"),
        ("Бишкек", "KG", 42.8746, 74.5698, "fru bishkek"),
        ("Душанбе", "TJ", 38.5598, 68.7870, "dyu dushanbe"),
        ("Кишинёв", "MD", 47.0105, 28.8638, "kiv chisinau"),
        ("Загреб", "HR", 45.8150, 15.9819, "zag zagreb"),
        ("Любляна", "SI", 46.0569, 14.5058, "lju ljubljana"),
        ("Ларнака", "CY", 34.9003, 33.6232, "lca larnaca nicosia"),
        ("Доха", "QA", 25.2854, 51.5310, "doh doha"),
        ("Эр-Рияд", "SA", 24.7136, 46.6753, "ruh riyadh"),
        ("Тегеран", "IR", 35.6892, 51.3890, "ika thr tehran"),
        ("Карачи", "PK", 24.8607, 67.0011, "khi karachi"),
        ("Джакарта", "ID", -6.2088, 106.8456, "cgk jakarta"),
        ("Куала-Лумпур", "MY", 3.1390, 101.6869, "kul kualalumpur"),
        ("Бангкок", "TH", 13.7563, 100.5018, "bkk bangkok"),
        ("Ханой", "VN", 21.0278, 105.8342, "han hanoi"),
        ("Манила", "PH", 14.5995, 120.9842, "mnl manila"),
        ("Лагос", "NG", 6.5244, 3.3792, "los lagos"),
        ("Найроби", "KE", -1.2921, 36.8219, "nbo nairobi"),
        ("Богота", "CO", 4.7110, -74.0721, "bog bogota"),
        ("Лима", "PE", -12.0464, -77.0428, "lim lima"),
        ("Кельн", "DE", 50.9375, 6.9603, "cgn cologne koeln"),
        ("Нюрнберг", "DE", 49.4521, 11.0767, "nue nuremberg nuernberg"),
        ("Штутгарт", "DE", 48.7758, 9.1829, "str stuttgart"),
        ("Лейпциг", "DE", 51.3397, 12.3731, "lej leipzig"),
        ("Сочи", "RU", 43.5855, 39.7231, "aer sochi"),
        ("Тюмень", "RU", 57.1530, 65.5343, "tjm tyumen"),
        ("Мурманск", "RU", 68.9585, 33.0827, "mmk murmansk"),
    ];

    private static readonly Dictionary<string, Place> Tokens = Build();

    // Codes that are real airports but too common as plain letters in hostnames to use as hints.
    private static readonly HashSet<string> CodeOnly = ["los", "lim", "han", "doh", "str", "bog", "aer", "kiv"];

    /// <summary>Location of an IATA/metro code, e.g. a Cloudflare colo ("FRA").</summary>
    public static GeoDto? ByCode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        return Tokens.TryGetValue(code.Trim().ToLowerInvariant(), out var p)
            ? new GeoDto(p.Lat, p.Lon, p.City, null, null, p.Cc, "code", "city")
            : null;
    }

    /// <summary>Known cities (for choosing your location manually).</summary>
    public static IEnumerable<(string City, string Cc, double Lat, double Lon)> KnownCities() =>
        Cities.Select(c => (c.City, c.Cc, c.Lat, c.Lon));

    // Interface / role words that look like codes but are not places.
    private static readonly HashSet<string> Stop =
    [
        "ae", "xe", "ge", "et", "te", "be", "po", "hu", "so", "ve", "vl", "vlan", "lo", "gi", "fa", "eth", "bundle",
        "cr", "ccr", "core", "edge", "bb", "br", "ar", "agg", "pe", "ce", "rt", "rtr", "gw", "ix", "ip", "net", "static",
        "dynamic", "host", "dsl", "cable", "pool", "cust", "customer", "transit", "peer", "peering", "border", "level",
        "the", "com", "org", "www", "mail", "ten", "one", "two", "and", "ipv", "mpls", "lag", "int", "ext", "srv", "dc",
    ];

    private static Dictionary<string, Place> Build()
    {
        var d = new Dictionary<string, Place>(StringComparer.Ordinal);
        foreach (var c in Cities)
            foreach (var t in c.Tokens.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                d.TryAdd(t, new Place(c.City, c.Cc, c.Lat, c.Lon));
        return d;
    }

    [GeneratedRegex("[a-z]+")]
    private static partial Regex Letters();

    /// <summary>Returns the place encoded in a router hostname, and the token that matched.</summary>
    public static (GeoDto Geo, string Token)? Match(string? hostname)
    {
        if (string.IsNullOrWhiteSpace(hostname)) return null;
        var labels = hostname.ToLowerInvariant().TrimEnd('.').Split('.');
        // Ignore the registered domain (last two labels): "cogentco.com", "ntt.net".
        var scan = labels.Length > 2 ? labels[..^2] : labels[..Math.Max(0, labels.Length - 1)];

        // Pass 1: whole multi-letter words (city names, carrier codes such as "frnkge").
        // Pass 2: three-letter codes, but only as an entire alphabetic run inside a label.
        foreach (var minLen in new[] { 4, 3 })
        {
            foreach (var label in scan.Reverse())
            {
                foreach (Match m in Letters().Matches(label))
                {
                    var w = m.Value;
                    if (w.Length < minLen || minLen == 4 && w.Length < 4 || minLen == 3 && w.Length != 3) continue;
                    if (Stop.Contains(w) || CodeOnly.Contains(w)) continue;
                    if (Tokens.TryGetValue(w, out var p))
                        return (new GeoDto(p.Lat, p.Lon, p.City, null, null, p.Cc, "hint", "approx"), w);
                    // Carrier style "frankfurt1", "ams3-": prefix of a known long token.
                    if (w.Length >= 6)
                    {
                        var prefix = Tokens.Keys.FirstOrDefault(k => k.Length >= 6 && w.StartsWith(k, StringComparison.Ordinal));
                        if (prefix != null)
                        {
                            var pp = Tokens[prefix];
                            return (new GeoDto(pp.Lat, pp.Lon, pp.City, null, null, pp.Cc, "hint", "approx"), prefix);
                        }
                    }
                }
            }
        }
        return null;
    }

    /// <summary>Great-circle distance in km.</summary>
    public static double DistanceKm(double lat1, double lon1, double lat2, double lon2)
    {
        const double R = 6371.0;
        var dLat = (lat2 - lat1) * Math.PI / 180;
        var dLon = (lon2 - lon1) * Math.PI / 180;
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                Math.Cos(lat1 * Math.PI / 180) * Math.Cos(lat2 * Math.PI / 180) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return 2 * R * Math.Asin(Math.Min(1, Math.Sqrt(a)));
    }

    /// <summary>
    /// Speed-of-light sanity check: light in fibre covers ~200 km per ms, so a node with minimum RTT r ms
    /// can be at most ~100·r km away (one way). Slack covers geolocation error and serialization delay.
    /// </summary>
    public static bool IsPhysicallyPossible(double distanceKm, double bestRttMs) =>
        distanceKm <= bestRttMs * 100.0 * 1.15 + 300;
}
