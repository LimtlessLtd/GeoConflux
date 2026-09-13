using System.Collections.Frozen;
using System.Text;

namespace Geopolitics.Infrastructure.Location;

/// <summary>
/// How closely a gazetteer coordinate describes whatever was reported there.
/// <para>
/// It exists because the entries are not comparable. "Odesa" is a city and its centroid is within a
/// few kilometres of anything reported in it; "Russia" is a country and its centroid is in central
/// Siberia. Storing both as plain coordinates would let the second be drawn on a globe exactly as
/// confidently as the first, which is a claim this data cannot support.
/// </para>
/// </summary>
public enum PlacePrecision
{
    /// <summary>A city or a specific feature. The centroid is close to what was reported.</summary>
    Settlement = 0,

    /// <summary>A named sea, strait, or region. The centroid is representative rather than exact.</summary>
    Region,

    /// <summary>A whole country. The centroid may be very far from the event.</summary>
    Country,
}

/// <param name="CanonicalName">Preferred display name for the place.</param>
/// <param name="Latitude">Representative latitude.</param>
/// <param name="Longitude">Representative longitude.</param>
/// <param name="CountryCode">ISO 3166-1 alpha-2 code, where the place sits in one country.</param>
/// <param name="Precision">How closely this coordinate describes a report placed here.</param>
public sealed record GazetteerEntry(
    string CanonicalName,
    double Latitude,
    double Longitude,
    string? CountryCode,
    PlacePrecision Precision = PlacePrecision.Settlement);

/// <summary>
/// What a report says about where it is, for resolving a name that means more than one place.
/// <para>
/// Both fields are evidence rather than instruction, and neither may produce a coordinate on its own.
/// A report's stated country narrows a list of candidates the lexicon already holds; it never adds a
/// candidate, moves one, or overrules a name that was not in doubt. That boundary is the same one
/// [ADR 005](../../../docs/adr/005-location-resolution.md) draws around a model naming a place.
/// </para>
/// </summary>
/// <param name="CountryCode">The ISO country code the report or its provider stated, when it stated one.</param>
/// <param name="Text">The report's own words, read for the other places it names.</param>
public sealed record PlaceContext(string? CountryCode = null, string? Text = null);

/// <summary>
/// The local place lexicon: chokepoints, seas, and cities that recur in geopolitical reporting.
/// Coordinates are representative centroids, adequate for a globe view at these zoom levels.
/// <para>
/// It is a single shared table because two components need the same names for different reasons.
/// The resolver turns a name into a position, which per ADR 005 only it may do. The mock enrichment
/// provider needs to <em>recognise</em> a name in text so the offline demo exercises the real
/// name-then-resolve path rather than a shortcut. Two copies of this list would drift, and the demo
/// would start proving something the production path does not do.
/// </para>
/// </summary>
public static class Gazetteer
{
    private static readonly GazetteerEntry[] Entries =
    [
        new("Bab-el-Mandeb", 12.585, 43.334, "DJ", PlacePrecision.Region),
        new("Strait of Hormuz", 26.567, 56.250, "OM", PlacePrecision.Region),
        new("Suez Canal", 30.500, 32.350, "EG", PlacePrecision.Region),
        new("Red Sea", 20.000, 38.000, null, PlacePrecision.Region),
        new("Gulf of Aden", 12.500, 47.500, null, PlacePrecision.Region),
        new("Black Sea", 43.000, 34.000, null, PlacePrecision.Region),
        new("Eastern Mediterranean", 34.700, 33.900, null, PlacePrecision.Region),
        new("South China Sea", 13.000, 114.000, null, PlacePrecision.Region),
        new("Taiwan Strait", 24.500, 119.500, null, PlacePrecision.Region),
        new("Strait of Malacca", 3.000, 100.500, "MY", PlacePrecision.Region),
        new("Gulf of Guinea", 3.000, 3.000, null, PlacePrecision.Region),
        new("Persian Gulf", 26.500, 51.500, null, PlacePrecision.Region),
        new("Baltic Sea", 57.500, 19.500, null, PlacePrecision.Region),
        new("Kerch Strait", 45.300, 36.500, null, PlacePrecision.Region),
        new("Panama Canal", 9.080, -79.680, "PA", PlacePrecision.Region),
        new("Sea of Japan", 40.000, 135.000, null, PlacePrecision.Region),
        new("Kyiv", 50.450, 30.523, "UA"),
        new("Odesa", 46.482, 30.723, "UA"),
        new("Kharkiv", 49.994, 36.230, "UA"),
        new("Donetsk", 48.016, 37.803, "UA"),
        new("Moscow", 55.756, 37.617, "RU"),
        new("Beijing", 39.904, 116.407, "CN"),
        new("Taipei", 25.033, 121.565, "TW"),
        new("Tehran", 35.689, 51.389, "IR"),
        new("Sanaa", 15.369, 44.191, "YE"),
        new("Aden", 12.785, 45.019, "YE"),
        new("Djibouti", 11.572, 43.145, "DJ"),
        new("Cairo", 30.044, 31.236, "EG"),
        new("Beirut", 33.888, 35.495, "LB"),
        new("Damascus", 33.513, 36.292, "SY"),
        new("Jerusalem", 31.769, 35.217, "IL"),
        new("Gaza", 31.504, 34.466, "PS"),
        new("Baghdad", 33.315, 44.366, "IQ"),
        new("Riyadh", 24.713, 46.675, "SA"),
        new("Ankara", 39.933, 32.859, "TR"),
        new("Istanbul", 41.008, 28.978, "TR"),
        new("Khartoum", 15.501, 32.559, "SD"),
        new("Mogadishu", 2.047, 45.318, "SO"),
        new("Bamako", 12.639, -8.003, "ML"),
        new("Lagos", 6.524, 3.379, "NG"),
        new("Manila", 14.600, 120.984, "PH"),
        new("Seoul", 37.567, 126.978, "KR"),
        new("Pyongyang", 39.039, 125.762, "KP"),
        new("Tokyo", 35.690, 139.692, "JP"),
        new("New Delhi", 28.614, 77.209, "IN"),
        new("Islamabad", 33.684, 73.048, "PK"),
        new("Kabul", 34.556, 69.208, "AF"),
        new("Caracas", 10.481, -66.904, "VE"),
        new("Port-au-Prince", 18.594, -72.307, "HT"),
        new("Brussels", 50.851, 4.352, "BE"),
        new("Warsaw", 52.230, 21.011, "PL"),
        new("Vilnius", 54.687, 25.280, "LT"),
        new("Helsinki", 60.170, 24.938, "FI"),
        new("Washington", 38.895, -77.037, "US"),
        new("London", 51.507, -0.128, "GB"),
        new("Paris", 48.857, 2.352, "FR"),
        new("Berlin", 52.520, 13.405, "DE"),

        // Country centroids, added so that real reporting — which names countries far more often
        // than it names straits — resolves at all. They are marked as country-level because a
        // single point for a whole country can sit hundreds of kilometres from the event, and a
        // dashboard that plotted them as though they were fixes would be lying about its precision.
        new("Afghanistan", 33.940, 67.710, "AF", PlacePrecision.Country),
        new("Albania", 41.150, 20.170, "AL", PlacePrecision.Country),
        new("Algeria", 28.030, 1.660, "DZ", PlacePrecision.Country),
        new("Angola", -11.200, 17.870, "AO", PlacePrecision.Country),
        new("Argentina", -38.420, -63.620, "AR", PlacePrecision.Country),
        new("Armenia", 40.070, 45.040, "AM", PlacePrecision.Country),
        new("Australia", -25.270, 133.780, "AU", PlacePrecision.Country),
        new("Austria", 47.520, 14.550, "AT", PlacePrecision.Country),
        new("Azerbaijan", 40.140, 47.580, "AZ", PlacePrecision.Country),
        new("Bangladesh", 23.680, 90.360, "BD", PlacePrecision.Country),
        new("Belarus", 53.710, 27.950, "BY", PlacePrecision.Country),
        new("Belgium", 50.500, 4.470, "BE", PlacePrecision.Country),
        new("Benin", 9.310, 2.320, "BJ", PlacePrecision.Country),
        new("Bolivia", -16.290, -63.590, "BO", PlacePrecision.Country),
        new("Bosnia and Herzegovina", 43.920, 17.680, "BA", PlacePrecision.Country),
        new("Botswana", -22.330, 24.680, "BW", PlacePrecision.Country),
        new("Brazil", -14.240, -51.930, "BR", PlacePrecision.Country),
        new("Bulgaria", 42.730, 25.490, "BG", PlacePrecision.Country),
        new("Burkina Faso", 12.240, -1.560, "BF", PlacePrecision.Country),
        new("Burundi", -3.370, 29.920, "BI", PlacePrecision.Country),
        new("Cambodia", 12.570, 104.990, "KH", PlacePrecision.Country),
        new("Cameroon", 7.370, 12.350, "CM", PlacePrecision.Country),
        new("Canada", 56.130, -106.350, "CA", PlacePrecision.Country),
        new("Central African Republic", 6.610, 20.940, "CF", PlacePrecision.Country),
        new("Chad", 15.450, 18.730, "TD", PlacePrecision.Country),
        new("Chile", -35.680, -71.540, "CL", PlacePrecision.Country),
        new("China", 35.860, 104.200, "CN", PlacePrecision.Country),
        new("Colombia", 4.570, -74.300, "CO", PlacePrecision.Country),
        new("Costa Rica", 9.750, -83.750, "CR", PlacePrecision.Country),
        new("Cote d'Ivoire", 7.540, -5.550, "CI", PlacePrecision.Country),
        new("Croatia", 45.100, 15.200, "HR", PlacePrecision.Country),
        new("Cuba", 21.520, -77.780, "CU", PlacePrecision.Country),
        new("Cyprus", 35.130, 33.430, "CY", PlacePrecision.Country),
        new("Czechia", 49.820, 15.470, "CZ", PlacePrecision.Country),
        new("Democratic Republic of the Congo", -4.040, 21.760, "CD", PlacePrecision.Country),
        new("Denmark", 56.260, 9.500, "DK", PlacePrecision.Country),
        new("Dominican Republic", 18.740, -70.160, "DO", PlacePrecision.Country),
        new("Ecuador", -1.830, -78.180, "EC", PlacePrecision.Country),
        new("Egypt", 26.820, 30.800, "EG", PlacePrecision.Country),
        new("El Salvador", 13.790, -88.900, "SV", PlacePrecision.Country),
        new("Eritrea", 15.180, 39.780, "ER", PlacePrecision.Country),
        new("Estonia", 58.600, 25.010, "EE", PlacePrecision.Country),
        new("Eswatini", -26.520, 31.470, "SZ", PlacePrecision.Country),
        new("Ethiopia", 9.150, 40.490, "ET", PlacePrecision.Country),
        new("Finland", 61.920, 25.750, "FI", PlacePrecision.Country),
        new("France", 46.230, 2.210, "FR", PlacePrecision.Country),
        new("Gabon", -0.800, 11.610, "GA", PlacePrecision.Country),
        new("Georgia", 42.320, 43.360, "GE", PlacePrecision.Country),
        new("Germany", 51.170, 10.450, "DE", PlacePrecision.Country),
        new("Ghana", 7.950, -1.020, "GH", PlacePrecision.Country),
        new("Greece", 39.070, 21.820, "GR", PlacePrecision.Country),
        new("Guatemala", 15.780, -90.230, "GT", PlacePrecision.Country),
        new("Guinea", 9.950, -9.700, "GN", PlacePrecision.Country),
        new("Haiti", 18.970, -72.290, "HT", PlacePrecision.Country),
        new("Honduras", 15.200, -86.240, "HN", PlacePrecision.Country),
        new("Hungary", 47.160, 19.500, "HU", PlacePrecision.Country),
        new("India", 20.590, 78.960, "IN", PlacePrecision.Country),
        new("Indonesia", -0.790, 113.920, "ID", PlacePrecision.Country),
        new("Iran", 32.430, 53.690, "IR", PlacePrecision.Country),
        new("Iraq", 33.220, 43.680, "IQ", PlacePrecision.Country),
        new("Ireland", 53.410, -8.240, "IE", PlacePrecision.Country),
        new("Israel", 31.050, 34.850, "IL", PlacePrecision.Country),
        new("Italy", 41.870, 12.570, "IT", PlacePrecision.Country),
        new("Japan", 36.200, 138.250, "JP", PlacePrecision.Country),
        new("Jordan", 30.590, 36.240, "JO", PlacePrecision.Country),
        new("Kazakhstan", 48.020, 66.920, "KZ", PlacePrecision.Country),
        new("Kenya", -0.020, 37.910, "KE", PlacePrecision.Country),
        new("Kosovo", 42.600, 20.900, "XK", PlacePrecision.Country),
        new("Kuwait", 29.310, 47.480, "KW", PlacePrecision.Country),
        new("Kyrgyzstan", 41.200, 74.770, "KG", PlacePrecision.Country),
        new("Laos", 19.860, 102.500, "LA", PlacePrecision.Country),
        new("Latvia", 56.880, 24.600, "LV", PlacePrecision.Country),
        new("Lebanon", 33.850, 35.860, "LB", PlacePrecision.Country),
        new("Liberia", 6.430, -9.430, "LR", PlacePrecision.Country),
        new("Libya", 26.340, 17.230, "LY", PlacePrecision.Country),
        new("Lithuania", 55.170, 23.880, "LT", PlacePrecision.Country),
        new("Madagascar", -18.770, 46.870, "MG", PlacePrecision.Country),
        new("Malawi", -13.250, 34.300, "MW", PlacePrecision.Country),
        new("Malaysia", 4.210, 101.980, "MY", PlacePrecision.Country),
        new("Mali", 17.570, -4.000, "ML", PlacePrecision.Country),
        new("Mauritania", 21.010, -10.940, "MR", PlacePrecision.Country),
        new("Mexico", 23.630, -102.550, "MX", PlacePrecision.Country),
        new("Moldova", 47.410, 28.370, "MD", PlacePrecision.Country),
        new("Mongolia", 46.860, 103.850, "MN", PlacePrecision.Country),
        new("Montenegro", 42.710, 19.370, "ME", PlacePrecision.Country),
        new("Morocco", 31.790, -7.090, "MA", PlacePrecision.Country),
        new("Mozambique", -18.670, 35.530, "MZ", PlacePrecision.Country),
        new("Myanmar", 21.920, 95.960, "MM", PlacePrecision.Country),
        new("Namibia", -22.960, 18.490, "NA", PlacePrecision.Country),
        new("Nepal", 28.390, 84.120, "NP", PlacePrecision.Country),
        new("Netherlands", 52.130, 5.290, "NL", PlacePrecision.Country),
        new("New Zealand", -40.900, 174.890, "NZ", PlacePrecision.Country),
        new("Nicaragua", 12.870, -85.210, "NI", PlacePrecision.Country),
        new("Niger", 17.610, 8.080, "NE", PlacePrecision.Country),
        new("Nigeria", 9.080, 8.680, "NG", PlacePrecision.Country),
        new("North Korea", 40.340, 127.510, "KP", PlacePrecision.Country),
        new("North Macedonia", 41.610, 21.750, "MK", PlacePrecision.Country),
        new("Norway", 60.470, 8.470, "NO", PlacePrecision.Country),
        new("Oman", 21.470, 55.980, "OM", PlacePrecision.Country),
        new("Pakistan", 30.380, 69.350, "PK", PlacePrecision.Country),
        new("Panama", 8.540, -80.780, "PA", PlacePrecision.Country),
        new("Papua New Guinea", -6.310, 143.960, "PG", PlacePrecision.Country),
        new("Paraguay", -23.440, -58.440, "PY", PlacePrecision.Country),
        new("Peru", -9.190, -75.020, "PE", PlacePrecision.Country),
        new("Philippines", 12.880, 121.770, "PH", PlacePrecision.Country),
        new("Poland", 51.920, 19.150, "PL", PlacePrecision.Country),
        new("Portugal", 39.400, -8.220, "PT", PlacePrecision.Country),
        new("Qatar", 25.350, 51.180, "QA", PlacePrecision.Country),
        new("Romania", 45.940, 24.970, "RO", PlacePrecision.Country),
        new("Russia", 61.520, 105.320, "RU", PlacePrecision.Country),
        new("Rwanda", -1.940, 29.870, "RW", PlacePrecision.Country),
        new("Saudi Arabia", 23.890, 45.080, "SA", PlacePrecision.Country),
        new("Senegal", 14.500, -14.450, "SN", PlacePrecision.Country),
        new("Serbia", 44.020, 21.010, "RS", PlacePrecision.Country),
        new("Sierra Leone", 8.460, -11.780, "SL", PlacePrecision.Country),
        new("Singapore", 1.350, 103.820, "SG", PlacePrecision.Country),
        new("Slovakia", 48.670, 19.700, "SK", PlacePrecision.Country),
        new("Slovenia", 46.150, 14.990, "SI", PlacePrecision.Country),
        new("Somalia", 5.150, 46.200, "SO", PlacePrecision.Country),
        new("South Africa", -30.560, 22.940, "ZA", PlacePrecision.Country),
        new("South Korea", 35.910, 127.770, "KR", PlacePrecision.Country),
        new("South Sudan", 6.880, 31.310, "SS", PlacePrecision.Country),
        new("Spain", 40.460, -3.750, "ES", PlacePrecision.Country),
        new("Sri Lanka", 7.870, 80.770, "LK", PlacePrecision.Country),
        new("Sudan", 12.860, 30.220, "SD", PlacePrecision.Country),
        new("Sweden", 60.130, 18.640, "SE", PlacePrecision.Country),
        new("Switzerland", 46.820, 8.230, "CH", PlacePrecision.Country),
        new("Syria", 34.800, 38.997, "SY", PlacePrecision.Country),
        new("Taiwan", 23.700, 120.960, "TW", PlacePrecision.Country),
        new("Tajikistan", 38.860, 71.280, "TJ", PlacePrecision.Country),
        new("Tanzania", -6.370, 34.890, "TZ", PlacePrecision.Country),
        new("Thailand", 15.870, 100.990, "TH", PlacePrecision.Country),
        new("Togo", 8.620, 0.820, "TG", PlacePrecision.Country),
        new("Tunisia", 33.890, 9.540, "TN", PlacePrecision.Country),
        new("Turkey", 38.960, 35.240, "TR", PlacePrecision.Country),
        new("Turkmenistan", 38.970, 59.560, "TM", PlacePrecision.Country),
        new("Uganda", 1.370, 32.290, "UG", PlacePrecision.Country),
        new("Ukraine", 48.380, 31.170, "UA", PlacePrecision.Country),
        new("United Arab Emirates", 23.420, 53.850, "AE", PlacePrecision.Country),
        new("United Kingdom", 55.380, -3.440, "GB", PlacePrecision.Country),
        new("United States", 37.090, -95.710, "US", PlacePrecision.Country),
        new("Uruguay", -32.520, -55.770, "UY", PlacePrecision.Country),
        new("Uzbekistan", 41.380, 64.590, "UZ", PlacePrecision.Country),
        new("Venezuela", 6.420, -66.590, "VE", PlacePrecision.Country),
        new("Vietnam", 14.060, 108.280, "VN", PlacePrecision.Country),
        new("Yemen", 15.550, 48.520, "YE", PlacePrecision.Country),
        new("Zambia", -13.130, 27.850, "ZM", PlacePrecision.Country),
        new("Zimbabwe", -19.020, 29.150, "ZW", PlacePrecision.Country),
    ];

    /// <summary>Common alternates, so ordinary reporting language resolves without an exact match.</summary>
    private static readonly (string Alias, string Canonical)[] Aliases =
    [
        ("Bab al-Mandab", "Bab-el-Mandeb"),
        ("Bab el Mandeb", "Bab-el-Mandeb"),
        ("Hormuz", "Strait of Hormuz"),
        ("Malacca Strait", "Strait of Malacca"),
        ("Kiev", "Kyiv"),
        ("Odessa", "Odesa"),
        ("Sana'a", "Sanaa"),
        ("Gaza Strip", "Gaza"),
        ("Washington DC", "Washington"),
        ("Mediterranean", "Eastern Mediterranean"),
        ("Levant", "Eastern Mediterranean"),
        ("DRC", "Democratic Republic of the Congo"),
        ("DR Congo", "Democratic Republic of the Congo"),
        ("Congo-Kinshasa", "Democratic Republic of the Congo"),
        ("Czech Republic", "Czechia"),
        ("Swaziland", "Eswatini"),
        ("Burma", "Myanmar"),
        ("Republic of Korea", "South Korea"),
        ("Democratic People's Republic of Korea", "North Korea"),
        ("DPRK", "North Korea"),
        ("UK", "United Kingdom"),
        ("Britain", "United Kingdom"),
        ("Great Britain", "United Kingdom"),
        ("USA", "United States"),
        ("US", "United States"),
        ("United States of America", "United States"),
        ("UAE", "United Arab Emirates"),
        ("Turkiye", "Turkey"),
        ("Holland", "Netherlands"),
        ("Macedonia", "North Macedonia"),
        ("Ivory Coast", "Cote d'Ivoire"),
        ("Occupied Palestinian Territory", "Gaza"),
        ("oPt", "Gaza"),
        ("West Bank", "Jerusalem"),

        // Transliterations that differ by who is writing. Neither spelling is a mistake, and
        // listing only one of a contested pair takes a position this table has no business taking.
        ("Kharkov", "Kharkiv"),
        ("Kharkiv Oblast", "Kharkiv"),
        ("Donetsk Oblast", "Donetsk"),
        ("Arabian Gulf", "Persian Gulf"),
        ("East Sea", "Sea of Japan"),
        ("Al-Quds", "Jerusalem"),
        ("Bab al-Mandeb", "Bab-el-Mandeb"),
    ];

    /// <summary>
    /// The same places as they are written in the languages that report them.
    /// <para>
    /// This table is what makes non-English collection worth doing. The enrichment prompt asks the
    /// model to report a place <em>as it is named in the text</em>, which is the right instruction
    /// and which produces an Arabic or Cyrillic name for an Arabic or Cyrillic source. Without these
    /// aliases such a name normalises to a key in its own script, finds nothing, and the observation
    /// is retained as unresolved — correct under ADR 005, and still a report that never reaches the
    /// globe. Reading twenty languages while resolving in one sees more of the world and plots less
    /// of it.
    /// </para>
    /// <para>
    /// Where a language writes a name two ways — simplified and traditional Han, Ukrainian and
    /// Russian Cyrillic, Arabic and Persian orthography for the same word — both are listed. Folding
    /// them mechanically is not possible: NFKC does not convert between Han character sets, and
    /// Cyrillic <c>ё</c> and <c>е</c> are distinct letters that reporting uses interchangeably.
    /// </para>
    /// </summary>
    private static readonly (string Alias, string Canonical)[] NativeScriptAliases =
    [
        // Arabic
        ("باب المندب", "Bab-el-Mandeb"),
        ("مضيق باب المندب", "Bab-el-Mandeb"),
        ("مضيق هرمز", "Strait of Hormuz"),
        ("قناة السويس", "Suez Canal"),
        ("البحر الأحمر", "Red Sea"),
        ("خليج عدن", "Gulf of Aden"),
        ("الخليج العربي", "Persian Gulf"),
        ("الخليج الفارسي", "Persian Gulf"),
        ("البحر المتوسط", "Eastern Mediterranean"),
        ("صنعاء", "Sanaa"),
        ("عدن", "Aden"),
        ("القاهرة", "Cairo"),
        ("بيروت", "Beirut"),
        ("دمشق", "Damascus"),
        ("بغداد", "Baghdad"),
        ("الرياض", "Riyadh"),
        ("غزة", "Gaza"),
        ("القدس", "Jerusalem"),
        ("الخرطوم", "Khartoum"),
        ("مقديشو", "Mogadishu"),
        ("إسطنبول", "Istanbul"),
        ("أنقرة", "Ankara"),
        ("جيبوتي", "Djibouti"),
        ("سوريا", "Syria"),
        ("اليمن", "Yemen"),
        ("مصر", "Egypt"),
        ("العراق", "Iraq"),
        ("لبنان", "Lebanon"),
        ("السودان", "Sudan"),

        // Persian. Shares the Arabic script but not all of its letters: Persian writes ک and ی
        // where Arabic writes ك and ي, so the same city needs both spellings to be found.
        ("تهران", "Tehran"),
        ("طهران", "Tehran"),
        ("تنگه هرمز", "Strait of Hormuz"),
        ("خلیج فارس", "Persian Gulf"),
        ("ایران", "Iran"),
        ("إيران", "Iran"),

        // Cyrillic
        ("Київ", "Kyiv"),
        ("Киев", "Kyiv"),
        ("Одеса", "Odesa"),
        ("Одесса", "Odesa"),
        ("Харків", "Kharkiv"),
        ("Харьков", "Kharkiv"),
        ("Донецьк", "Donetsk"),
        ("Донецк", "Donetsk"),
        ("Москва", "Moscow"),
        ("Чёрное море", "Black Sea"),
        ("Черное море", "Black Sea"),
        ("Керченский пролив", "Kerch Strait"),
        ("Балтийское море", "Baltic Sea"),
        ("Україна", "Ukraine"),
        ("Украина", "Ukraine"),
        ("Россия", "Russia"),
        ("Сирия", "Syria"),

        // Han, simplified and traditional
        ("北京", "Beijing"),
        ("台北", "Taipei"),
        ("臺北", "Taipei"),
        ("南海", "South China Sea"),
        ("南中国海", "South China Sea"),
        ("南中國海", "South China Sea"),
        ("台湾海峡", "Taiwan Strait"),
        ("臺灣海峽", "Taiwan Strait"),
        ("马六甲海峡", "Strait of Malacca"),
        ("馬六甲海峽", "Strait of Malacca"),
        ("霍尔木兹海峡", "Strait of Hormuz"),
        ("霍爾木茲海峽", "Strait of Hormuz"),
        ("苏伊士运河", "Suez Canal"),
        ("蘇伊士運河", "Suez Canal"),
        ("红海", "Red Sea"),
        ("紅海", "Red Sea"),
        ("日本海", "Sea of Japan"),
        ("德黑兰", "Tehran"),
        ("德黑蘭", "Tehran"),
        ("莫斯科", "Moscow"),
        ("东京", "Tokyo"),
        ("東京", "Tokyo"),
        ("平壤", "Pyongyang"),
        ("中国", "China"),
        ("中國", "China"),
        ("美国", "United States"),
        ("美國", "United States"),
        ("俄罗斯", "Russia"),
        ("俄羅斯", "Russia"),
        ("日本", "Japan"),
        ("台湾", "Taiwan"),
        ("臺灣", "Taiwan"),
        ("乌克兰", "Ukraine"),
        ("烏克蘭", "Ukraine"),

        // Hangul
        ("서울", "Seoul"),
        ("평양", "Pyongyang"),
        ("한국", "South Korea"),
        ("북한", "North Korea"),

        // Devanagari
        ("नई दिल्ली", "New Delhi"),
        ("भारत", "India"),
        ("पाकिस्तान", "Pakistan"),
    ];

    /// <summary>
    /// Spellings the sourced extract does not carry, for places it does.
    /// <para>
    /// The extract is only as good as the alternate labels the source happens to hold, and for small
    /// places it frequently holds none. Tigray is the sharp case the plan called out: Latin
    /// transliteration of Tigrinya and Amharic names is genuinely unstable, so a town appears as
    /// Zalambesa in one report and Zalambessa in the next, and a lexicon holding one of them places
    /// half the reporting and drops the rest.
    /// </para>
    /// <para>
    /// This is the editorial layer doing what it is for. Asserting that two spellings name one place
    /// is a judgement a person can make and defend; it is emphatically not the same act as writing a
    /// coordinate, which stays sourced. The target must already exist in the extract, so an entry
    /// here can add a way of saying a name and can never add a place or move one.
    /// </para>
    /// </summary>
    private static readonly (string Alias, string Target)[] SourcedAliases =
    [
        // Tigray, where the source holds a single Latin spelling and reporting uses several.
        ("Zalambesa", "Zalambessa"),
        ("Zalambassa", "Zalambessa"),
        ("Mekelle", "Mekele"),
        ("Mek'ele", "Mekele"),
    ];

    /// <summary>
    /// Every spelling the lexicon knows, paired with the place it denotes.
    /// <para>
    /// Built once and shared by both entry points, so a name that resolves cannot be a name that is
    /// not searched for, or the reverse. Keeping two parallel constructions in step by hand was fine
    /// at two hundred entries and is not at several thousand.
    /// </para>
    /// </summary>
    /// <summary>
    /// Names that denote more than one place, with the places they could denote.
    /// <para>
    /// Declared before <see cref="Names"/> because both of these are filled while it is being built,
    /// and a static field initialiser runs in declaration order. Written once, during that build, and
    /// read-only thereafter.
    /// </para>
    /// </summary>
    private static readonly Dictionary<string, GazetteerEntry[]> Contested =
        new(StringComparer.Ordinal);

    /// <summary>
    /// The contested spellings worth hunting for in prose. Held apart from <see cref="Names"/>
    /// because they resolve to no single entry, which is the whole of what makes them contested.
    /// </summary>
    private static readonly List<string> ContestedSpellings = [];

    private static readonly (string Name, GazetteerEntry Entry, bool Searchable)[] Names = BuildNames();

    /// <summary>
    /// Names in the sourced layer that denote more than one place and were therefore dropped. Counted
    /// rather than discarded silently, because it is a real and reportable limit on coverage.
    /// </summary>
    public static int AmbiguousSourcedNames { get; private set; }

    private static readonly FrozenDictionary<string, GazetteerEntry> Lookup = BuildLookup();

    /// <summary>
    /// Every searchable spelling with the entry it denotes, longest first so that "Strait of Hormuz"
    /// is preferred over the bare "Hormuz" it contains.
    /// </summary>
    private static readonly (string Term, string Reported)[] SearchTerms = BuildSearchTerms();

    /// <summary>
    /// The index over those terms. Built once, at first touch, and then never rebuilt.
    /// <para>
    /// ADR 026 measured the linear scan it replaces and said what would end it: the scan is linear in
    /// the size of the lexicon, so "a lexicon ten times this size would cost five milliseconds per
    /// observation and the algorithm would need replacing". Sprint 10 made the lexicon twenty times
    /// larger. See <see cref="PlaceNameAutomaton"/>.
    /// </para>
    /// </summary>
    private static readonly PlaceNameAutomaton Automaton =
        PlaceNameAutomaton.Build([.. SearchTerms.Select(pair => pair.Term)]);

    /// <summary>
    /// How many spellings the prose scan hunts for. Reported because it, rather than the number of
    /// places, is what the scan's cost and its collision risk both scale with.
    /// </summary>
    public static int SearchTermCount => SearchTerms.Length;

    /// <summary>How many states the index over those spellings holds, so its cost stays measurable.</summary>
    public static int AutomatonStates => Automaton.States;

    public static bool TryResolve(string? name, out GazetteerEntry entry)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            entry = null!;
            return false;
        }

        return Lookup.TryGetValue(NormaliseKey(name), out entry!);
    }

    /// <summary>
    /// How many place names a report is read for before its context is taken as established. A long
    /// report naming two hundred places says no more about which Springfield is meant than its first
    /// dozen do.
    /// </summary>
    private const int ContextMentionLimit = 12;

    /// <summary>
    /// Resolves a name using what the report says about where it is from.
    /// <para>
    /// This exists because global coverage broke an assumption the three-theatre lexicon could hold.
    /// With 2,750 places, a name meaning two places was rare enough to drop; across 246 countries
    /// there are dozens of Victorias and San Josés, and dropping all of them would mean the coarse
    /// layer could not place the very reports it was added for.
    /// </para>
    /// <para>
    /// So a contested name is answered from context or not at all. Nothing here lowers the bar for
    /// asserting a coordinate: a name that no context settles still resolves to nothing, exactly as
    /// it did before, and the context-free overload above is untouched. The evidence is ranked, and
    /// the order is the order of how much it establishes — the country a report states about itself
    /// outranks a country inferred from the other places it happens to mention.
    /// </para>
    /// </summary>
    public static bool TryResolve(string? name, PlaceContext context, out GazetteerEntry entry)
    {
        ArgumentNullException.ThrowIfNull(context);

        // A name that resolves without context resolves the same way with it. Context narrows a
        // contested name; it may never overrule a settled one, because that would let a report's
        // dateline move a place that was never in doubt.
        if (TryResolve(name, out entry))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(name)
            || !Contested.TryGetValue(NormaliseKey(name), out var candidates))
        {
            return false;
        }

        if (Narrow(candidates, context.CountryCode) is { } declared)
        {
            entry = declared;
            return true;
        }

        foreach (var country in CountriesNamedIn(context.Text))
        {
            if (Narrow(candidates, country) is { } mentioned)
            {
                entry = mentioned;
                return true;
            }
        }

        entry = null!;
        return false;
    }

    /// <summary>
    /// The one place in this country that this name denotes, or nothing.
    /// <para>
    /// Nothing rather than a choice, where a country holds two genuinely different places with the
    /// name: that is exactly the case context cannot settle, and picking the larger would be the
    /// confident misplacement the refusal exists to avoid. The dominant-population rule already ran
    /// before the name was called contested at all.
    /// </para>
    /// <para>
    /// Two records of the <em>same</em> place are not that case, and the two sourced layers make it
    /// common: Oleksandriia arrives from the theatre extract and Oleksandriya from the global one,
    /// same town, coordinates a few hundred metres apart. Reading those as a country that cannot make
    /// up its mind would refuse a name nothing is actually ambiguous about — so the same tolerance
    /// <see cref="Disambiguate"/> uses for duplicates applies here too.
    /// </para>
    /// </summary>
    private static GazetteerEntry? Narrow(GazetteerEntry[] candidates, string? country)
    {
        if (string.IsNullOrWhiteSpace(country))
        {
            return null;
        }

        GazetteerEntry? only = null;

        foreach (var candidate in candidates)
        {
            if (!string.Equals(candidate.CountryCode, country, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (only is not null && !IsSamePlace(only, candidate))
            {
                return null;
            }

            only ??= candidate;
        }

        return only;
    }

    /// <summary>
    /// The countries of the places this text names, most-specific first.
    /// <para>
    /// Only settled names vote. A contested mention cannot establish a country, because which country
    /// it is in is the question — letting one vouch for another would let two ambiguities agree with
    /// each other and produce a confident answer out of nothing.
    /// </para>
    /// </summary>
    private static IEnumerable<string> CountriesNamedIn(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            yield break;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var mentions = new List<(PlacePrecision Precision, string Country)>();

        foreach (var term in Automaton.FindAll(FoldForSearch(text), ContextMentionLimit))
        {
            if (Lookup.TryGetValue(NormaliseKey(SearchTerms[term].Reported), out var mentioned)
                && mentioned.CountryCode is { } country
                && seen.Add(country))
            {
                mentions.Add((mentioned.Precision, country));
            }
        }

        // A settlement named in the same report is better evidence than a country named in it: "the
        // strike near Kramatorsk" says more about where this is than a later mention of Russia does.
        foreach (var (_, country) in mentions.OrderBy(mention => mention.Precision))
        {
            yield return country;
        }
    }

    /// <summary>
    /// Finds the first place this text names, or <see langword="null"/> when it names none.
    /// <para>
    /// Substring matching, not tokenisation: the lexicon holds multi-word names and the input is
    /// prose. The earliest mention wins because reporting states where something happened before it
    /// lists which other places reacted to it.
    /// </para>
    /// </summary>
    public static string? FindFirstMention(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        // Folded once, then compared ordinally: the terms were folded the same way when the table
        // was built, so an Arabic presentation form in the text meets the standard form stored here.
        var term = Automaton.FindFirst(FoldForSearch(text));

        return term < 0 ? null : SearchTerms[term].Reported;
    }

    /// <summary>Case- and punctuation-insensitive key, so "Bab el Mandeb" matches "Bab-el-Mandeb".</summary>
    public static string NormaliseKey(string value)
    {
        Span<char> buffer = value.Length <= 128 ? stackalloc char[value.Length] : new char[value.Length];
        var length = 0;

        foreach (var character in value)
        {
            if (char.IsLetterOrDigit(character))
            {
                buffer[length++] = char.ToLowerInvariant(character);
            }
        }

        return new string(buffer[..length]);
    }

    /// <summary>
    /// How far apart two records with the same name may sit and still be taken for one place, in
    /// degrees — roughly five kilometres.
    /// <para>
    /// The sourced layer contains genuine duplicates: the same town entered twice in Wikidata with
    /// coordinates that differ in the fourth decimal. Treating those as a name that means two places
    /// would throw away a real place for the sake of a rounding difference. Two records this close
    /// describe one town at globe zoom whichever of them is kept.
    /// </para>
    /// </summary>
    private const double SamePlaceDegrees = 0.05;

    /// <summary>
    /// How far apart nested administrative units sharing a name may sit — roughly 160 km, which
    /// comfortably holds a governorate and the district and town inside it that carry its name.
    /// </summary>
    private const double NestedDegrees = 1.5;

    /// <summary>
    /// How many times larger one candidate must be than every other before its name is taken to mean
    /// it. Reporting that says "Kostiantynivka" without qualification means the town of sixty-seven
    /// thousand on the front line, not one of the four villages that share the name — but the margin
    /// has to be wide enough that this is a fact about the places rather than a coin toss.
    /// </summary>
    private const int DominantPopulationRatio = 10;

    /// <summary>
    /// The floor under that rule. Ten times the size of nothing much is still nothing much, and a
    /// hamlet of three hundred should not win a name from four hamlets of thirty.
    /// </summary>
    private const int DominantPopulationFloor = 5_000;

    /// <summary>
    /// Merges the curated table with the sourced extract, under the rules ADR 026 settles.
    /// <para>
    /// The two layers behave differently on a collision, and deliberately. A collision inside the
    /// curated table is a mistake — it is small, hand-written, and argued for — so it throws at first
    /// touch, in every test, rather than becoming a wrong pin on a globe. A collision inside the
    /// sourced extract is not a mistake at all: a country has many villages sharing a name, and there
    /// is no editorial answer to choose between them. Those names are dropped, because an unplaced
    /// report is visibly unplaced while a confidently misplaced one is not.
    /// </para>
    /// <para>
    /// Where the two layers name the same place the curated entry wins, because somebody chose it.
    /// </para>
    /// </summary>
    private static (string Name, GazetteerEntry Entry, bool Searchable)[] BuildNames()
    {
        var curated = new Dictionary<string, GazetteerEntry>(StringComparer.Ordinal);
        var names = new List<(string Name, GazetteerEntry Entry, bool Searchable)>(Entries.Length);

        // Curated spellings are always searchable. They are few, every one of them was chosen
        // deliberately, and the two-letter ones already have the word-boundary handling that makes
        // "US" safe to look for. The size of that table is what keeps it trustworthy.
        foreach (var entry in Entries)
        {
            curated[NormaliseKey(entry.CanonicalName)] = entry;
            names.Add((entry.CanonicalName, entry, true));
        }

        foreach (var (alias, canonical) in Aliases.Concat(NativeScriptAliases))
        {
            var key = NormaliseKey(alias);
            var target = curated[NormaliseKey(canonical)];

            // Two aliases normalising to one key would silently place a city somewhere else, and
            // the reader of the map would have no way to tell. With the lexicon now spanning six
            // scripts the chance of an accidental collision is real, so it fails here — at first
            // touch, in every test — rather than becoming a wrong pin on a globe.
            if (curated.TryGetValue(key, out var existing) && existing != target)
            {
                throw new InvalidOperationException(
                    $"Gazetteer alias '{alias}' collides with an entry already resolving to '{existing.CanonicalName}'.");
            }

            curated[key] = target;
            names.Add((alias, target, true));
        }

        var sourced = SourcedNames(curated);
        names.AddRange(sourced);
        names.AddRange(SourcedAliasNames(curated, sourced));
        return [.. names];
    }

    /// <summary>
    /// Attaches the editorial spellings above to the sourced places they name.
    /// <para>
    /// Applied after the extract has been merged, because that is the only point at which the targets
    /// exist. An alias whose target is missing is a mistake in this table rather than a fact about the
    /// world, and it throws for the same reason a curated alias collision does: it is small, hand
    /// written, and fixable at first touch.
    /// </para>
    /// </summary>
    private static List<(string Name, GazetteerEntry Entry, bool Searchable)> SourcedAliasNames(
        Dictionary<string, GazetteerEntry> curated,
        List<(string Name, GazetteerEntry Entry, bool Searchable)> sourced)
    {
        var byKey = new Dictionary<string, GazetteerEntry>(StringComparer.Ordinal);

        foreach (var (name, entry, _) in sourced)
        {
            byKey[NormaliseKey(name)] = entry;
        }

        var added = new List<(string Name, GazetteerEntry Entry, bool Searchable)>(SourcedAliases.Length);

        foreach (var (alias, target) in SourcedAliases)
        {
            if (!byKey.TryGetValue(NormaliseKey(target), out var entry))
            {
                throw new InvalidOperationException(
                    $"Gazetteer alias '{alias}' names '{target}', which is not in the extract. Either the "
                    + "extract has changed or the target is misspelt; an alias may add a spelling but "
                    + "never a place.");
            }

            var key = NormaliseKey(alias);

            // The curated table and an unambiguous sourced name both outrank this, because both are
            // already a settled answer for that spelling.
            if (curated.ContainsKey(key) || byKey.ContainsKey(key))
            {
                continue;
            }

            byKey[key] = entry;

            // Hand-written, so trusted in prose exactly as the curated aliases are.
            added.Add((alias, entry, true));
        }

        return added;
    }

    /// <summary>
    /// The sourced layer, with curated names left alone and ambiguous ones removed.
    /// </summary>
    /// <summary>
    /// A sourced name together with the facts that exist only to settle a collision. Rank and
    /// population ride along here rather than on <see cref="GazetteerEntry"/> because the curated
    /// layer shares that type and has no use for either, and because carrying them in a static side
    /// table would make the lexicon depend on the order its own fields happen to be declared in.
    /// </summary>
    private readonly record struct Candidate(
        string Name,
        GazetteerEntry Entry,
        int Rank,
        int Population,
        bool Searchable,
        bool Deep);

    /// <summary>
    /// At or below this length a sourced spelling is too short to hunt for in running prose unless
    /// the place is well known. Four characters is where real place names stop being distinctive:
    /// the extract contains villages genuinely named Sad, Rama, Gora, Aura and Bile, and aliases
    /// including Luck, Mare and Musa.
    /// </summary>
    private const int ShortNameLength = 4;

    /// <summary>
    /// How many inhabitants a place needs before its short name is worth searching prose for. Set so
    /// that Kyiv, Lviv, Sumy, Uman, Aden, Ibb, Axum and Adwa are all found and a hamlet called Sad is
    /// not.
    /// </summary>
    private const int ShortNamePopulationFloor = 20_000;

    /// <summary>
    /// The shortest spelling worth hunting for in a script written without spaces between words.
    /// <para>
    /// This is the per-language decision the global coverage assessment asked for, and the measured
    /// shape of the lexicon is what settles where the line falls. Of the coarse layer's spellings,
    /// 2.6% of Latin ones are four characters or shorter, 6.3% of Cyrillic and 8.5% of Arabic — so
    /// holding those scripts to the existing floor costs almost nothing. For Han it is 71% and for
    /// Hangul 97%, because those scripts carry a word in two or three characters. One floor for both
    /// groups is wrong whichever value it takes: it either admits ordinary words or excludes most of
    /// two scripts.
    /// </para>
    /// <para>
    /// The split follows a distinction the lexicon already draws for a related reason. A script
    /// written without word breaks gets no word-edge check when it is matched, because there is no
    /// edge to find — which is precisely why a short spelling in one is dangerous, and why the floor
    /// here is three rather than two. 北京 and 서울 are two characters and are found regardless: they
    /// are curated entries, chosen deliberately, and the curated layer is searchable unconditionally.
    /// </para>
    /// </summary>
    private const int UnspacedShortNameLength = 3;

    /// <summary>
    /// A place from a sourced layer, in the one shape the merge understands.
    /// <para>
    /// Two extracts feed this now and they do not share a record type — one carries a theatre and a
    /// Wikidata id, the other a country and nothing else. Mapping both onto this keeps one merge
    /// rather than two that would drift.
    /// </para>
    /// </summary>
    /// <param name="Deep">
    /// Whether this place comes from a layer somebody tasked. It decides one thing: whether a short
    /// Latin spelling of it may be hunted for in running prose. See <see cref="IsSearchable"/>.
    /// </param>
    private readonly record struct SourcedPlace(
        string Name,
        double Latitude,
        double Longitude,
        string? CountryCode,
        PlacePrecision Precision,
        int Rank,
        int Population,
        bool Deep,
        IReadOnlyList<string> Aliases);

    /// <summary>
    /// How many countries a contested name may keep candidates for.
    /// </summary>
    private const int ContestedCountryLimit = 24;

    /// <summary>
    /// How many candidates a contested name keeps per country.
    /// <para>
    /// Two, and the second one is doing a job the first cannot. Context narrows by country, so one
    /// candidate per country would be enough to answer — and would quietly turn "this country holds
    /// two places with this name" into a confident choice between them. Keeping a second is what lets
    /// that case still be recognised and refused.
    /// </para>
    /// </summary>
    private const int ContestedCandidatesPerCountry = 2;

    /// <summary>
    /// The sourced layers, merged under the rules ADR 026 settles and ADR 033 tiers.
    /// <para>
    /// Every candidate for a name is collected before any of them is chosen, because ambiguity is
    /// only visible once they have all been seen. Choosing as they arrive would leave whichever
    /// extract was read first holding the name.
    /// </para>
    /// </summary>
    private static List<(string Name, GazetteerEntry Entry, bool Searchable)> SourcedNames(
        Dictionary<string, GazetteerEntry> curated)
    {
        var candidates = new Dictionary<string, List<Candidate>>(StringComparer.Ordinal);

        Collect(
            TheatrePlaces.All.Select(place => new SourcedPlace(
                place.Name,
                place.Latitude,
                place.Longitude,
                place.CountryCode,
                place.Precision,
                place.Rank,
                place.Population ?? 0,
                true,
                place.Aliases)),
            curated,
            candidates);

        Collect(
            GlobalPlaces.All.Select(place => new SourcedPlace(
                place.Name,
                place.Latitude,
                place.Longitude,
                place.CountryCode,
                place.Precision,
                place.Rank,
                place.Population ?? 0,
                false,
                place.Aliases)),
            curated,
            candidates);

        var accepted = new List<(string Name, GazetteerEntry Entry, bool Searchable)>(candidates.Count);
        var contested = 0;

        foreach (var (key, forKey) in candidates)
        {
            var pool = Deciding(forKey);

            if (Choose(pool) is { } chosen)
            {
                accepted.Add((chosen.Name, chosen.Entry, chosen.Searchable));
                continue;
            }

            contested++;

            Contested[key] = Bound(pool);

            // The spelling still goes into the prose scan, so that a report naming a contested place
            // is recognised as naming somewhere at all. What it resolves to is then a question for
            // the context, and the answer may still be "not enough to say".
            if (pool.Find(candidate => candidate.Searchable) is { Searchable: true } searchable)
            {
                ContestedSpellings.Add(searchable.Name);
            }
        }

        AmbiguousSourcedNames = contested;
        return accepted;
    }

    /// <summary>
    /// The candidates a contested name keeps, bounded so that a name shared by five hundred villages
    /// does not carry five hundred entries.
    /// <para>
    /// Bounded by country rather than by size, which is the correction to a first attempt that simply
    /// kept the largest dozen. Context narrows by country, so keeping the largest dozen discards
    /// precisely the candidate a report is most likely to need: there are more than twelve places
    /// called Alexandria larger than Oleksandriia, so a Ukrainian report naming Alexandria could not
    /// be resolved by the very mechanism built to resolve it.
    /// </para>
    /// </summary>
    private static GazetteerEntry[] Bound(List<Candidate> candidates) =>
        [.. candidates
            .GroupBy(candidate => candidate.Entry.CountryCode ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(group => group.Max(candidate => candidate.Population))
            .Take(ContestedCountryLimit)
            .SelectMany(group => group
                .OrderByDescending(candidate => candidate.Population)
                .Take(ContestedCandidatesPerCountry))
            .Select(candidate => candidate.Entry)];

    /// <summary>
    /// Which place this name denotes, or that it denotes none clearly enough to say.
    /// <para>
    /// The ordinary rules decide it wherever they can. What is left is the case the global layer
    /// created: a name held by both a tasked theatre and some larger place in another country, where
    /// no rule about duplicates, nesting or population applies because the two are simply different
    /// places with the same name.
    /// </para>
    /// <para>
    /// There the tasked layer wins, because somebody pointed this system at that theatre and a report
    /// naming Pokrovsk is overwhelmingly likely to be about the Pokrovsk being fought over. It is an
    /// editorial position and is stated as one rather than arrived at by accident.
    /// </para>
    /// <para>
    /// It has a limit, and the limit is what stops it becoming the Victoria problem. A Ukrainian
    /// hamlet of 1,042 people shares its name with Hong Kong's Victoria and with the Australian
    /// state; letting the tasked layer hold that name would put reporting about either of them on a
    /// hamlet. So the deep candidate is preferred only where nothing else dominates it, by the same
    /// margin the population rule already uses everywhere else.
    /// </para>
    /// </summary>
    private static Candidate? Choose(List<Candidate> candidates)
    {
        if (Disambiguate(candidates) is { } chosen)
        {
            return chosen;
        }

        var deep = candidates.FindAll(candidate => candidate.Deep);

        // Collapsed by the ordinary rules first, because the tasked layer routinely holds one place
        // several times over: Marib is a governorate, a district and a city, and asking whether there
        // is exactly one deep candidate would answer "no" for the very places this is here to keep.
        if (deep.Count == 0 || Disambiguate(deep) is not { } tasked)
        {
            return null;
        }

        var largest = candidates.Max(candidate => candidate.Population);

        return largest >= DominantPopulationFloor
            && largest >= tasked.Population * (long)DominantPopulationRatio
                ? null
                : tasked;
    }

    /// <summary>
    /// Which candidates a name is actually decided between, once layer depth is taken into account.
    /// <para>
    /// The deep layer wins <em>inside its own country</em> and nowhere else, and both halves of that
    /// are load-bearing.
    /// </para>
    /// <para>
    /// Inside one country it must win, because the two layers describe the same place at different
    /// scales: a Ukrainian town and the raion around it share a name 1,398 times over, and the nested
    /// rule below would resolve every one of them to the raion centroid. That would quietly coarsen
    /// the front-line towns the theatre layer was bought to place precisely.
    /// </para>
    /// <para>
    /// Across countries it must not, and the first attempt at this sprint got it wrong by letting it.
    /// The theatre extract holds a Ukrainian village called Niu-York with 9,917 inhabitants, a hamlet
    /// called Victoria with 1,042, and Oleksandriia, which is spelled Alexandria. Giving the deep
    /// layer those names outright meant "New York", "Victoria" and "Alexandria" resolved to them — a
    /// marker placed in the wrong country at full confidence, which ADR 026 is explicit is worse than
    /// no marker at all. Pooled instead, the dominant-population rule settles all three correctly, and
    /// where nothing dominates the name becomes contested and the context decides.
    /// </para>
    /// </summary>
    private static List<Candidate> Deciding(List<Candidate> candidates)
    {
        var country = candidates[0].Entry.CountryCode;

        foreach (var candidate in candidates)
        {
            if (!string.Equals(candidate.Entry.CountryCode, country, StringComparison.OrdinalIgnoreCase))
            {
                return candidates;
            }
        }

        var deep = candidates.FindAll(candidate => candidate.Deep);

        return deep.Count > 0 ? deep : candidates;
    }

    /// <summary>
    /// Reads one sourced layer into the shared candidate pool, skipping keys the curated table owns.
    /// </summary>
    private static void Collect(
        IEnumerable<SourcedPlace> places,
        Dictionary<string, GazetteerEntry> curated,
        Dictionary<string, List<Candidate>> candidates)
    {
        foreach (var place in places)
        {
            var entry = new GazetteerEntry(
                place.Name,
                place.Latitude,
                place.Longitude,
                place.CountryCode,
                place.Precision);

            foreach (var name in place.Aliases.Prepend(place.Name))
            {
                var isPreferredName = name == place.Name;
                var key = NormaliseKey(name);

                // A name that normalises to nothing — punctuation or digits only — cannot be looked
                // up, and the curated layer owns any key it already holds.
                if (key.Length == 0 || curated.ContainsKey(key))
                {
                    continue;
                }

                if (!candidates.TryGetValue(key, out var forKey))
                {
                    candidates[key] = forKey = [];
                }

                forKey.Add(new Candidate(
                    name,
                    entry,
                    place.Rank,
                    place.Population,
                    IsSearchable(name, isPreferredName, place.Population, place.Deep),
                    place.Deep));
            }
        }
    }

    /// <summary>
    /// Whether a sourced spelling is worth hunting for in running prose, as opposed to merely worth
    /// resolving when a caller names it.
    /// <para>
    /// The two entry points are asking different questions, and ADR 026 put the difference exactly: a
    /// caller passing "Sad" to <see cref="TryResolve"/> has asserted that it is a place name, while
    /// the scanner finding "sad" inside a sentence has guessed. Sprint 10 is where that distinction
    /// stopped being a refinement and became the rule. <b>Only a layer somebody chose is hunted for
    /// in prose.</b>
    /// </para>
    /// <para>
    /// It was measured rather than argued. Running the scan over every committed corpus of realistic
    /// prose in this repository — the evaluation fixtures, the severity corpus and the replay
    /// observations — 38 coarse-layer names fired, and almost every one was an ordinary English word
    /// that is also a real administrative unit somewhere: <c>Exchange</c>, <c>Police</c>, <c>Along</c>
    /// (a town in Arunachal Pradesh), <c>Maritime</c> (a region of Togo), <c>Centre</c> (a region of
    /// Cameroon), <c>Northern</c>, <c>Village</c>, <c>Union</c>, <c>Legal</c>, <c>Burns</c>, and a run
    /// of American counties — Early, Power, Gates, Ferry, Cross, Sharp — leaking in through their
    /// bare-word aliases. Two narrower rules were tried first: a length floor lets <c>Frontier</c> and
    /// <c>University</c> through, and dropping alternate spellings only moved 38 to 24. The collision
    /// is intrinsic to holding every administrative unit on earth, because a great many of them are
    /// named after ordinary words.
    /// </para>
    /// <para>
    /// Almost nothing is lost by this, which is why it is the right trade rather than a retreat. The
    /// coarse layer exists to place reports whose location is <em>named</em> — by a coded dataset, by
    /// an enrichment provider, or by a submission — and every one of those arrives through
    /// <see cref="TryResolve"/>, which holds all 78,547 places and every spelling of them. What it no
    /// longer does is let the offline provider guess a district out of raw prose, and the measurement
    /// above is what that guess was actually worth.
    /// </para>
    /// <para>
    /// Within the deep layers the floor is per script, which is the per-language decision the global
    /// coverage assessment asked for. A four-character floor suits Latin and suits Arabic; it would
    /// exclude 71% of Han spellings and 97% of Hangul ones, because those scripts carry a word in two
    /// or three characters.
    /// </para>
    /// </summary>
    private static bool IsSearchable(string name, bool isPreferredName, int population, bool deep) =>
        deep
        && (WritesWithoutWordBreaks(name)
            ? name.Length >= UnspacedShortNameLength
            : name.Length > ShortNameLength
                || (isPreferredName && population >= ShortNamePopulationFloor));

    /// <summary>
    /// Whether every letter in this spelling belongs to a script written without word breaks.
    /// <para>
    /// Every letter rather than any, because a name mixing scripts — a Han name with a Latin
    /// qualifier after it — has Latin word edges in it and is held to the Latin floor. The strict
    /// reading is the safe one: it can only ever demand that a name be longer.
    /// </para>
    /// </summary>
    private static bool WritesWithoutWordBreaks(string name)
    {
        var letters = false;

        foreach (var character in name)
        {
            if (!char.IsLetter(character))
            {
                continue;
            }

            if (!PlaceNameAutomaton.WritesWithoutWordBreaks(character))
            {
                return false;
            }

            letters = true;
        }

        return letters;
    }

    /// <summary>
    /// Decides which place a shared name denotes, or that it denotes none usefully.
    /// <para>
    /// Three rules, each answering a different reason two rows can carry one name, and a refusal when
    /// none of them applies. The refusal is the important part: resolving to whichever candidate came
    /// back first would put a pin in the wrong place at full confidence, and a reader has no way to
    /// tell that from a right one.
    /// </para>
    /// </summary>
    private static Candidate? Disambiguate(List<Candidate> candidates)
    {
        var first = candidates[0];

        // One place, entered twice. The source genuinely holds duplicates of the same town whose
        // coordinates differ in the fourth decimal, and dropping a real place over a rounding
        // difference would be absurd. At globe zoom either row draws the same dot.
        if (candidates.All(candidate => IsSamePlace(first.Entry, candidate.Entry)))
        {
            return first;
        }

        // Nested administrative units. Marib is a governorate, a district and a city; Taiz is a city
        // and a governorate. These are not competing places, they are one place described at three
        // scales, so the containing unit is chosen — it is the answer that is certainly right, and
        // its Region precision already tells the reader it is an area rather than a position.
        var nested = candidates
            .Where(candidate => candidates.All(other => IsNested(candidate.Entry, other.Entry)))
            .ToArray();

        if (nested.Length == candidates.Count)
        {
            var smallestRank = candidates.Min(candidate => candidate.Rank);

            if (candidates.Count(candidate => candidate.Rank == smallestRank) == 1)
            {
                return candidates.First(candidate => candidate.Rank == smallestRank);
            }
        }

        // Genuinely different places that happen to share a name, where one of them is what anyone
        // saying the bare name means. The margin is deliberately wide: this is meant to catch a town
        // among villages, not to pick a winner between two towns.
        var ordered = candidates
            .OrderByDescending(candidate => candidate.Population)
            .ToArray();

        var leader = ordered[0].Population;
        var runnerUp = ordered[1].Population;

        return leader >= DominantPopulationFloor && leader >= runnerUp * DominantPopulationRatio
            ? ordered[0]
            : null;
    }

    private static bool IsSamePlace(GazetteerEntry first, GazetteerEntry second) =>
        Math.Abs(first.Latitude - second.Latitude) <= SamePlaceDegrees
        && Math.Abs(first.Longitude - second.Longitude) <= SamePlaceDegrees;

    private static bool IsNested(GazetteerEntry first, GazetteerEntry second) =>
        Math.Abs(first.Latitude - second.Latitude) <= NestedDegrees
        && Math.Abs(first.Longitude - second.Longitude) <= NestedDegrees;

    /// <summary>
    /// Indexes every known spelling by its normalised key.
    /// <para>
    /// Assignment rather than <c>Add</c>, because several spellings legitimately normalise to one key
    /// while meaning the same place: the key strips punctuation and case, so "Bab-el-Mandeb", "Bab el
    /// Mandeb" and "Bab al-Mandeb" are one key and one entry. The collisions that would actually be
    /// wrong — two spellings meaning two different places — have already been dealt with by the time
    /// this runs: the curated layer throws on them and the sourced layer drops them.
    /// </para>
    /// </summary>
    private static FrozenDictionary<string, GazetteerEntry> BuildLookup()
    {
        var map = new Dictionary<string, GazetteerEntry>(Names.Length, StringComparer.Ordinal);

        // Every spelling, including the short ones the prose scan declines to hunt for. Asking
        // TryResolve about "Ibb" is a caller stating that it is a place name; finding "ibb" inside a
        // sentence is the system guessing, and only the second needs protecting against.
        foreach (var (name, entry, _) in Names)
        {
            map[NormaliseKey(name)] = entry;
        }

        return map.ToFrozenDictionary(StringComparer.Ordinal);
    }

    /// <summary>
    /// Every searchable spelling with the name a scan reports for it, longest first so that "Strait
    /// of Hormuz" is preferred over the bare "Hormuz" it contains.
    /// <para>
    /// A settled name reports the canonical name of the place it denotes, which is what the resolver
    /// then looks up. A contested spelling reports itself, because there is no single place to name
    /// yet — deciding which one is what the context-scoped overload of <see cref="TryResolve"/> is
    /// for, and it needs the spelling that was actually found to do it.
    /// </para>
    /// <para>
    /// Settled names come first so that where a contested spelling folds onto a settled one, the
    /// settled answer is the one kept.
    /// </para>
    /// </summary>
    private static (string Term, string Reported)[] BuildSearchTerms()
    {
        var terms = new List<(string Term, string Reported)>(Names.Length + ContestedSpellings.Count);

        foreach (var (name, entry, searchable) in Names)
        {
            if (searchable)
            {
                Add(name, entry.CanonicalName);
            }
        }

        foreach (var spelling in ContestedSpellings)
        {
            Add(spelling, spelling);
        }

        // Longest first, so "Strait of Hormuz" is preferred over the bare "Hormuz" inside it, and so
        // that where two spellings fold to one string the longer-named one is the survivor.
        return [.. terms.OrderByDescending(item => item.Term.Length)];

        void Add(string spelling, string reported)
        {
            var folded = FoldForSearch(spelling);
            terms.Add((folded, reported));

            // Reporting writes Ma'rib and Marib, Sana'a and Sanaa, Ta'izz and Taizz, and a lexicon
            // holding one of them places half the reporting and drops the rest.
            //
            // The two entry points had drifted apart on exactly this. NormaliseKey strips punctuation,
            // so a caller asking about either spelling resolves; the prose scan keeps punctuation,
            // because word edges are what stop "us" matching inside "because" — and so it could only
            // ever find the spelling the extract happened to record. Marib is the case that matters:
            // ADR 026 notes it is what the fighting in that theatre is mostly about, and the extract
            // spells it Ma'rib.
            var plain = WithoutApostrophes(folded);

            // Held to the same floor the spelling itself was, because taking a character out makes it
            // shorter and the floor is about length. Luts'k is romanised Luc'k, which without its
            // apostrophe is "luck" — an ordinary English word that would then outrank every real place
            // name in any sentence wishing anybody any.
            if (!string.Equals(plain, folded, StringComparison.Ordinal)
                && IsSearchable(plain, isPreferredName: false, population: 0, deep: true))
            {
                terms.Add((plain, reported));
            }
        }
    }

    /// <summary>
    /// The same spelling with the apostrophes taken out, in every form a source might write one.
    /// <para>
    /// Only apostrophes, and deliberately not the general punctuation stripping the lookup key uses.
    /// Removing hyphens and spaces as well would make "Bab el Mandeb" searchable as "babelmandeb",
    /// which no text contains, and would cost the word boundaries that keep short names safe.
    /// </para>
    /// </summary>
    private static string WithoutApostrophes(string value)
    {
        if (value.AsSpan().IndexOfAny(Apostrophes) < 0)
        {
            return value;
        }

        Span<char> buffer = value.Length <= 128 ? stackalloc char[value.Length] : new char[value.Length];
        var length = 0;

        foreach (var character in value)
        {
            if (Apostrophes.IndexOf(character) < 0)
            {
                buffer[length++] = character;
            }
        }

        return new string(buffer[..length]);
    }

    /// <summary>Straight, typographic and modifier-letter forms, all of which appear in real extracts.</summary>
    private static ReadOnlySpan<char> Apostrophes => "'\u2018\u2019\u02bc\u02bb\u00b4`";

    /// <summary>
    /// Case- and compatibility-folded form used for searching prose, keeping punctuation and spacing
    /// so word edges remain visible.
    /// <para>
    /// NFKC rather than NFC, because the forms that actually break matching are compatibility ones:
    /// Arabic presentation forms, full-width Latin in CJK copy, and CJK compatibility ideographs all
    /// fold here onto the spellings this lexicon stores. NFC would leave every one of them alone.
    /// </para>
    /// </summary>
    private static string FoldForSearch(string value)
    {
        try
        {
            return value.Normalize(NormalizationForm.FormKC).ToLowerInvariant();
        }
        catch (ArgumentException)
        {
            // Feed text is not guaranteed to be well-formed Unicode. An unpaired surrogate is not a
            // reason to lose the whole observation, so search the text as it came.
            return value.ToLowerInvariant();
        }
    }

}
