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

    private static readonly FrozenDictionary<string, GazetteerEntry> Lookup = BuildLookup();

    /// <summary>
    /// Every searchable spelling with the entry it denotes, longest first so that "Strait of Hormuz"
    /// is preferred over the bare "Hormuz" it contains.
    /// </summary>
    private static readonly (string Term, GazetteerEntry Entry)[] SearchTerms = BuildSearchTerms();

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
        var haystack = FoldForSearch(text);

        var bestIndex = int.MaxValue;
        string? bestName = null;

        foreach (var (term, entry) in SearchTerms)
        {
            var index = haystack.IndexOf(term, StringComparison.Ordinal);

            // Keep looking past a hit that landed inside a longer word. The first occurrence of
            // "us" may be in "because" while the sentence goes on to name the United States.
            while (index >= 0 && !IsWholeMention(haystack, index, term.Length))
            {
                index = haystack.IndexOf(term, index + 1, StringComparison.Ordinal);
            }

            if (index >= 0 && index < bestIndex)
            {
                bestIndex = index;
                bestName = entry.CanonicalName;
            }
        }

        return bestName;
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

    private static FrozenDictionary<string, GazetteerEntry> BuildLookup()
    {
        var map = new Dictionary<string, GazetteerEntry>(StringComparer.Ordinal);

        foreach (var entry in Entries)
        {
            map[NormaliseKey(entry.CanonicalName)] = entry;
        }

        foreach (var (alias, canonical) in Aliases.Concat(NativeScriptAliases))
        {
            var key = NormaliseKey(alias);
            var target = map[NormaliseKey(canonical)];

            // Two aliases normalising to one key would silently place a city somewhere else, and
            // the reader of the map would have no way to tell. With the lexicon now spanning six
            // scripts the chance of an accidental collision is real, so it fails here — at first
            // touch, in every test — rather than becoming a wrong pin on a globe.
            if (map.TryGetValue(key, out var existing) && existing != target)
            {
                throw new InvalidOperationException(
                    $"Gazetteer alias '{alias}' collides with an entry already resolving to '{existing.CanonicalName}'.");
            }

            map[key] = target;
        }

        return map.ToFrozenDictionary(StringComparer.Ordinal);
    }

    private static (string Term, GazetteerEntry Entry)[] BuildSearchTerms()
    {
        var aliases = Aliases.Concat(NativeScriptAliases).ToArray();
        var terms = new List<(string Term, GazetteerEntry Entry)>(Entries.Length + aliases.Length);
        terms.AddRange(Entries.Select(entry => (FoldForSearch(entry.CanonicalName), entry)));

        foreach (var (alias, canonical) in aliases)
        {
            terms.Add((FoldForSearch(alias), Entries.First(entry => entry.CanonicalName == canonical)));
        }

        return [.. terms.OrderByDescending(item => item.Term.Length)];
    }

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

    /// <summary>
    /// True when a match at this position is a mention rather than a fragment of a longer word.
    /// <para>
    /// Without this the two-letter aliases are catastrophic: <c>US</c> occurs inside "because",
    /// "thus", and "Russia", and because the search takes the earliest match across every term, one
    /// such hit outranks the real place name later in the sentence.
    /// </para>
    /// </summary>
    private static bool IsWholeMention(string text, int index, int length) =>
        !RunsIntoWord(text, index - 1, text[index])
        && !RunsIntoWord(text, index + length, text[index + length - 1]);

    private static bool RunsIntoWord(string text, int neighbourIndex, char termEdge)
    {
        if (neighbourIndex < 0 || neighbourIndex >= text.Length)
        {
            return false;
        }

        var neighbour = text[neighbourIndex];

        if (!char.IsLetterOrDigit(neighbour))
        {
            return false;
        }

        // Scripts written without spaces have no word edge to find. Requiring one would mean never
        // matching 美国 inside 在美国发生, or 서울 inside 서울에서 — which is to say, never matching
        // them at all, since that is how those languages are written.
        return !WritesWithoutWordBreaks(neighbour) && !WritesWithoutWordBreaks(termEdge);
    }

    private static bool WritesWithoutWordBreaks(char character) => character
        is (>= '぀' and <= 'ヿ')      // Hiragana and Katakana
        or (>= '㐀' and <= '䶿')      // CJK unified ideographs, extension A
        or (>= '一' and <= '鿿')      // CJK unified ideographs
        or (>= '가' and <= '힯')      // Hangul syllables, which take particles unspaced
        or (>= '豈' and <= '﫿')      // CJK compatibility ideographs
        or (>= '฀' and <= '๿');     // Thai
}
