using System.Collections.Generic;

namespace HorizonServiceMonitor;

/// <summary>
/// 최초 실행 시(법인이 하나도 없을 때) 시드하는 13개 법인 기본 목록.
/// server_url은 자리표시자이므로 '설정'에서 실제 커넥션서버(또는 LB) 주소와
/// 모니터링 계정(도메인/사용자/비밀번호)을 입력한 뒤 활성화한다.
/// </summary>
public static class DefaultCorporations
{
    // (코드, 리전, 자리표시자 커넥션서버)
    private static readonly (string Code, string Region, string Host)[] Sites =
    {
        ("HQ",  "아시아 태평양", "cs-hq.example.com"),
        ("OC1", "아시아 태평양", "cs-oc1.example.com"),
        ("OC2", "아시아 태평양", "cs-oc2.example.com"),
        ("GM1", "아시아 태평양", "cs-gm1.example.com"),
        ("GM2", "아시아 태평양", "cs-gm2.example.com"),
        ("HD",  "아시아 태평양", "cs-hd.example.com"),
        ("WA",  "유럽",         "cs-wa.example.com"),
        ("NJ",  "북미",         "cs-nj.example.com"),
        ("MI",  "북미",         "cs-mi.example.com"),
        ("AZ",  "북미",         "cs-az.example.com"),
        ("ST",  "북미",         "cs-st.example.com"),
        ("HM",  "북미",         "cs-hm.example.com"),
        ("NA",  "북미",         "cs-na.example.com"),
    };

    public static void SeedIfEmpty(Database db)
    {
        if (db.CountCorps() > 0) return;
        foreach (var c in Build()) db.UpsertCorp(c);
    }

    public static IReadOnlyList<Corporation> Build()
    {
        var list = new List<Corporation>();
        var sort = 0;
        foreach (var s in Sites)
            list.Add(new Corporation
            {
                Name = $"{s.Code} 법인", Code = s.Code, Region = s.Region,
                ServerUrl = "https://" + s.Host,
                IntervalSec = 60, TimeoutMs = 15000,
                Enabled = false, // 자리표시자 → 실제 주소/계정 입력 후 활성화
                CollectInventory = true,
                Sort = sort++,
            });
        return list;
    }
}
