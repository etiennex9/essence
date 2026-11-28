using Essence.Hours;
using Essence.Ingest.Hours.Plugins;

namespace Essence.Ingest.Tests;

/// <summary>Hours as people write them, in French and English: read for sure, or refused with the reason.</summary>
public sealed class HoursTextTests
{
    private static readonly TimeRange[] NoMarker = [];

    [Theory]
    [InlineData("Lun-Ven : 6 h à 22 h; Sam. et dim. : 7 h à 21 h", "Mo-Fr 06:00-22:00; Sa-Su 07:00-21:00")]
    [InlineData("Lundi au vendredi de 6h00 à 22h00\nSamedi et dimanche de 7h00 à 21h00", "Mo-Fr 06:00-22:00; Sa-Su 07:00-21:00")]
    [InlineData("Du lundi au vendredi : 6 h 30 à 23 h 30<br>Fin de semaine : 8 h à minuit", "Mo-Fr 06:30-23:30; Sa-Su 08:00-24:00")]
    [InlineData("Mon-Fri 6am-10pm, Sat-Sun 7:00 a.m. - 9:30 p.m.", "Mo-Fr 06:00-22:00; Sa-Su 07:00-21:30")]
    [InlineData("Monday to Friday: 6:00 AM to 10:00 PM | Saturday & Sunday: 7:00 AM to 9:00 PM", "Mo-Fr 06:00-22:00; Sa-Su 07:00-21:00")]
    [InlineData("Tous les jours de 6 h à 23 h", "Mo-Su 06:00-23:00")]
    [InlineData("6h-23h tous les jours", "Mo-Su 06:00-23:00")]
    [InlineData("Ouvert de 6 h à 23 h", "Mo-Su 06:00-23:00")]
    [InlineData("Ouvert 24 h/24, 7j/7", "24/7")]
    [InlineData("24/7", "24/7")]
    [InlineData("24 heures", "24/7")]
    [InlineData("Open 24 hours", "24/7")]
    [InlineData("Lun-Sam 6h-22h; Dim fermé", "Mo-Sa 06:00-22:00")]
    [InlineData("En semaine 5h-23h; fin de semaine 7h-23h", "Mo-Fr 05:00-23:00; Sa-Su 07:00-23:00")]
    [InlineData("Lun-Ven 6h-12h et 13h-21h; Sam-Dim 9h-17h", "Mo-Fr 06:00-12:00,13:00-21:00; Sa-Su 09:00-17:00")]
    [InlineData("Ven-Sam 6h-2h; Dim-Jeu 6h-23h", "Mo-Th 06:00-23:00; Fr-Sa 06:00-02:00; Su 06:00-23:00")]
    [InlineData("Lun-Dim 6h-22h\nJours fériés : 9h-17h", "Mo-Su 06:00-22:00")]
    [InlineData("Tous les jours 6 h – 22 h, fermé les jours fériés", "Mo-Su 06:00-22:00")]
    [InlineData("Lundi : midi à minuit; mardi-dimanche : 6h-22h", "Mo 12:00-24:00; Tu-Su 06:00-22:00")]
    [InlineData("Lun-Sam 6h-22h; Dimanche et jours fériés 8h-20h", "Mo-Sa 06:00-22:00; Su 08:00-20:00")]
    [InlineData("Mon-Sun 6:00 am - 22:00", "Mo-Su 06:00-22:00")]
    [InlineData("Mon-Sun 11am - midnight", "Mo-Su 11:00-24:00")]
    [InlineData("Lun-Dim 11 h à midi", "Mo-Su 11:00-12:00")]
    [InlineData("Mon-Sun noon - 6pm", "Mo-Su 12:00-18:00")]
    public void Hours_written_by_people_are_read(string text, string rule)
    {
        Assert.Equal(rule, HoursText.Rule(text, NoMarker, out var error), StringComparer.Ordinal);
        Assert.Null(error);
    }

    [Theory]
    [InlineData("Tous les jours 6h-23h sauf l'été", "\"sauf\"")]
    [InlineData("Lun-Ven 6h-22h", "Sunday's hours aren't given")]
    [InlineData("Lun-Ven 6h-22h; Lundi 7h-21h", "Monday's hours are given twice")]
    [InlineData("Lun-Dim 6h-14h et 12h-22h", "overlap")]
    [InlineData("Fermé", "closed every day")]
    [InlineData("6 - 22", "isn't understood")]
    [InlineData("Lun-Ven 6h-22h; 7h-21h", "aren't given days")]
    [InlineData("Lun-Ven", "aren't given hours")]
    [InlineData("Été : 6h-23h", "\"ete\"")]
    [InlineData("Lun-Dim 6h-22h; Dimanche et jours fériés 8h-20h", "Sunday's hours are given twice")]
    [InlineData("Lun-Dim 6h-22h; Dimanche et fériés : fermé", "Sunday's hours are given twice")]
    [InlineData("Mon-Sun 1:00-6:00 pm", "12-hour clock")]
    [InlineData("Mon-Sun 6:00 am - 10:00", "12-hour clock")]
    [InlineData("Mon-Sun noon - 6:00", "12-hour clock")]
    [InlineData("Mon-Sun 6am-10pm, 1:00-6:00", "12-hour clock")]
    [InlineData("Mon-Sun 11:00 - midnight", "12-hour clock")]
    public void Hours_that_can_t_be_read_for_sure_are_refused_with_the_reason(string text, string reason)
    {
        Assert.Null(HoursText.Rule(text, NoMarker, out var error));
        Assert.Contains(reason, error, StringComparison.Ordinal);
    }

    [Fact]
    public void A_range_the_source_writes_when_it_doesn_t_say_refuses_the_hours()
    {
        Assert.Null(HoursText.Rule("Lun-Ven 6h-22h; Sam-Dim 0h-12h", [new TimeRange(0, 720)], out var error));
        Assert.Contains("which the source gives when it doesn't say", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("06:00", false, 360)]
    [InlineData("6:00:00", false, 360)]
    [InlineData("6h", false, 360)]
    [InlineData("6 h 30", false, 390)]
    [InlineData("06H30", false, 390)]
    [InlineData("0630", false, 390)]
    [InlineData("6:30 p.m.", false, 1110)]
    [InlineData("12am", false, 0)]
    [InlineData("12am", true, 1440)]
    [InlineData("00:00", true, 1440)]
    [InlineData("24:00", true, 1440)]
    [InlineData("midi", false, 720)]
    [InlineData("Minuit", true, 1440)]
    public void Times_are_read_in_every_way_they_re_written(string text, bool end, int minute)
    {
        Assert.True(HoursText.TryTime(text, end, out var read));
        Assert.Equal(minute, read);
    }

    [Theory]
    [InlineData("24:00", false)]
    [InlineData("25:00", true)]
    [InlineData("13pm", false)]
    [InlineData("6", false)]
    [InlineData("6:75", false)]
    public void Times_that_aren_t_are_refused(string text, bool end) => Assert.False(HoursText.TryTime(text, end, out _));

    [Theory]
    [InlineData("https://schema.org/Monday", DayOfWeek.Monday)]
    [InlineData("Dim.", DayOfWeek.Sunday)]
    [InlineData("MERCREDI", DayOfWeek.Wednesday)]
    [InlineData("samedis", DayOfWeek.Saturday)]
    [InlineData("Thurs", DayOfWeek.Thursday)]
    public void Days_are_read_in_French_English_and_schema_org(string name, DayOfWeek day)
    {
        Assert.True(HoursText.TryDay(name, out var read));
        Assert.Equal(day, read);
    }

    [Fact]
    public void The_whole_day_from_midnight_to_midnight_and_a_range_of_no_length_isn_t()
    {
        Assert.True(HoursText.TryRange("00:00", "00:00", out var all));
        Assert.Equal(TimeRange.AllDay, all);
        Assert.False(HoursText.TryRange("06:00", "06:00", out _));
    }
}
