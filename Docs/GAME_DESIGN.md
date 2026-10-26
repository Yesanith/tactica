# Tactica — Genel Oyun Tasarımı

Oct 25, 2026 · @Leo

> **Repodaki kopya.** Oyunun tasarımı burada; kodun bugün ne yaptığı [ARCHITECTURE.md](ARCHITECTURE.md),
> fazlar ve yol haritası [PROGRESS.md](PROGRESS.md), FFT araştırması [FFT_DESIGN_NOTES.md](FFT_DESIGN_NOTES.md).
> 2026-10-25: kodla çelişen birkaç yer düzeltildi ve etiketlendi (Override formülü, düşman tablosu,
> Wait'in ödülleri). Atıf yapılan ayrıntı dosyaları (sınıf ağacı, Ek A, düşman tasarımı, hikâye
> taslağı) henüz repoda değil.
> 2026-10-25 (ikinci geçiş): Faz 8–15 koda girdi; "Mevcut / Planlı" etiketleri buna göre
> güncellendi. Tasarım kararları ve öneriler değiştirilmedi.

## Vizyon

Tactica, FFT'nin ızgara savaşını ve job sistemini siberpunk bir şehre taşıyan, harita ve hikâye tabanlı bir taktik RPG. Oyuncu bir sokak ekibini yönetir; düşmanı yenmek kadar onu **hack'leyip kendi tarafına geçirmek** de kazanma yoludur.

Tasarım sütunları:

1. **Konum önemlidir.** Yön, yükseklik ve menzil her turda karar verdirir; arkadan vuruş ödüllendirilir.
2. **Her sınıfın kimliği vardır.** Sınıf istatistiği, silahı ve yetenek menüsü birlikte bir rol tanımlar; hiçbir sınıf her şeyde iyi değildir.
3. **Kadro büyür ve şekillenir.** JP ile yetenek öğrenilir, sınıflar zincirle açılır, hack ile düşmandan yeni birim kazanılır.
4. **Harita ve hikâye.** Mahalleler hikâyeyle açılır; her savaş kısa sürer ve tek bir hedefi vardır.
5. **FFT'yi kopyalama, nedenini anla.** Alınan her mekanik bir soruya cevap verir; sığmayanlar (Brave/Faith, cinsiyete bağlı job'lar) bilerek dışarıda bırakıldı.

Platform: Unity 6 (URP), PC, yalnızca Input System. Şu an oynanabilir bir prototip; menüler bilerek çıplak.

### Bu dokümanın kapsamı ve etiketler

Bu metin Tactica'nın tüm tasarımını tek yerde toplar: oyunun ne olduğu, nasıl çalıştığı ve nereye gittiği. Her yerde şu etiketler geçer:

- **Mevcut:** kodda çalışıyor (repo `Yesanith/tactica`, 2026-10-25 çalışma kopyası; Faz 15'e kadar).
- **Planlı:** tasarım kararı verildi, kodda yok.
- **Öneri:** onay bekliyor; senin kararla değişebilir.

### FFT'den ne alındı, ne farklı

| Mekanik | FFT | Tactica |
| --- | --- | --- |
| Başlangıç job'ları | 2 kök (Squire, Chemist) | 5 başlangıç sınıfı |
| Ağaç | Zincir, üstte birleşme noktaları | Aynı: orta sınıflar Lv2–3, üst sınıflar iki ya da üç sınıfın birleşimi |
| JP | Etkili eylemle kazanılır, yetenekler satın alınır | Aynı; kazanılanın 1/4'ü kadroya |
| Slotlar | Primary, Secondary, Reaction, Support, Movement | Aynı |
| Ekipman | Job'a bağlı; "Equip" Support yetenekleriyle genişler | Aynı (7 silah türü) |
| İstatistik | Ham değer × job çarpanı | Durumsuz formül: sınıf tabanı + büyüme × seviye |
| Özel karakterler | Kendi job'larıyla gelir, değiştirebilir | Sınıfı kilitli |
| Brave / Faith | Reaksiyon oranı, büyü gücü | Yok; reaksiyonların sabit yüzdesi var |
| Hack | Invite ile birim kazanma | System Override: taraf değiştirme, Netrunner'a ait |
| Dünya | Dünya haritası, rastgele savaşlar | Mahalle haritası, yol savaşları, hikâye, ilanlar |
| Takım | Savaşta en fazla 5 | 4 (kadro 8) |

### Hedef deneyim (öneri)

- Her savaş kısa bir bulmacadır: konum, yön, yükseklik ve sıra doğru kullanılınca kazanılır.
- Kadroyu oyuncu şekillendirir: sınıf, hibrit, slot ve silah seçimi önemlidir.
- Hack yaratıcı bir çözümdür, ama bedeli vardır (MP, şans, hikâyedeki anlamı).
- Hikâye ve harita, savaşlara bağlam verir; her savaşın bir nedeni vardır.

## Oyun döngüsü

```
Harita (hazırlık) ──▶ Hedef seç (hikâye / ilan / yolculuk) ──▶ Savaş ──▶ Sonuç (JP, loot, kayıt) ──▶ Harita
                                     │                            │
                                     └── yolculukta yol kesilebilir └── Override ile düşman kazanılabilir
```

Oyuncu haritada kadroyu hazırlar, bir hedef seçer (hikâye, ilan ya da bir mahalleye yolculuk) ve savaşa girer; yolculukta yol kesilebilir. Savaşta Override ile düşman birimini kazanabilir. Sonuç ekranı JP ve loot verir, ilerleme kaydedilir ve oyuncu haritaya döner.

## Savaş sistemi

Savaş, iki taraf arasında CT (charge time) ile sıralanan, ızgara tabanlı bir sıra sistemidir.

| Kural | Tasarım |
| --- | --- |
| Izgara | Seed'e bağlı üretilen harita (5 geçiş); hareket ve menzil Manhattan mesafesiyle, tırmanma Jump değeriyle sınırlı |
| Sıra | Her birim Speed kadar CT biriktirir; 100'e ulaşan oynar. Tur bedeli yapılan eyleme göre 100, 80 veya 60; artan CT en fazla 60 devreder |
| Yön ve kaçınma | Önden Evasion tam, yandan yarım, arkadan etkisiz; savunma duruşu (Wait) Evasion'ı ikiye katlar |
| Hasar | `max(1, Power + Strength (ya da Tech) + silah Power − Defense, Power'ın dörtte biri)` - Defense vuruşun en fazla dörtte üçünü emer; Tech'e ölçeklenen yetenekler (hack hasarı, şifa, Reboot) Strength yerine Tech kullanır |
| Ölüm | Düşen birim bayılır; 3 tur içinde Reboot alamazsa kalıcı olarak elenir (flatline) |
| Fraksiyon | Player, Enemy, Neutral; misafir birimler (guest) oyuncu tarafında ama AI kontrolündedir |

Savaş şu hedeflerden biriyle biter: tüm düşmanları yen (DefeatAll), hedef birimi yen ya da hack'le (DefeatTarget), korunan birimi hayatta tut (Protect).

Düşman AI'ı her olası eylemi puanlar (beklenen hasar, öldürme, hedefin rolü, şifa, buff, debuff, hack); ayrıntı aşağıda "Düşman AI'ı" bölümünde.

Her savaş bir **yerleştirme** adımıyla başlar (Mevcut): ekip, kendi kenarındaki turkuaz bir bölgeye (ekip başına 2 kare, en az 6) yerleştirilir; misafirler ve düşmanlar sabittir. Ardından ilk tur başlar.

### Tur akışı ve kontroller (mevcut)

Bir turda sırayla **Move** (hareket menzilini gör, tile'a tıkla), **Act** (yetenek seç, hedefe tıkla) ve **Wait** (turu bitirir) kullanılır. Wait, bu tur eylem yapmadıysan bir savunma duruşu verir: **+5 Defense ve Evasion ×2**, birimin bir sonraki turuna kadar. Üstüne, eylem yapmayan tur daha az CT harcar (100 yerine 80 ya da 60), yani birim sıraya daha erken döner. Bu üç ödül birlikte fazla olabilir; FFT yalnızca CT avantajı verir. Yön seçimi geldi (Faz 11) ve karar: Wait duruşunu korur, yön seçimi yeni bir ödül eklemez. Her eylem açık bir buton basışı ister; reddedilen yetenek hiçbir şeye mal olmaz.

| Giriş | İşlev |
| --- | --- |
| Sol fare tuşu | Tile / hedef seç, hareketi onayla |
| Sağ fare tuşu veya Esc | Açık menüyü ya da yarım kalan komutu iptal et |
| 1 / 2 / 3 | Act / Move / Wait |
| WASD veya ok tuşları | Kamerayı kaydır |
| Fare tekerleği | Yakınlaştır / uzaklaştır |
| Q / E | Kamerayı 90° döndür |
| HUD: Act / Move / Wait | Yetenek menüsü, hareket, bekle; harcanan eylem gri görünür |

Sıra şeridi (Turn Queue) CT tablosunun bir kopyasını ileri simüle ederek sonraki turları gösterir; hedef önizlemesi (örn. "7 dmg -> 8/15", "41% to hack") sonuçla aynı formülleri okuduğu için sonuçla çelişmez. Bayılmış birimler de CT kazanır; flatline sayıcısı aynı saat üzerinde işler. Bayılmış birim, flatline olana ya da Reboot edilene kadar tile'ı işgal eder.

### Savaş derinliği (Mevcut, Faz 11)

Eskiden eksik olan savaş özellikleri koda girdi:

- **Durum etkileri:** Buff, Debuff, Stun, zamanla hasar/iyileşme ve Taunt; süreler etkilenen birimin kendi turlarıyla sayılır.
- **Alan hasarı (AoE):** zararlı patlamalar alandaki herkese vurur (dost ateşi açık, önizlemede "ALLY!" uyarısı).
- **Reaksiyonlar:** Counter ve Brace; her reaksiyonun kendi tetik yüzdesi var, karşılığa karşılık verilmez.
- **Tech ölçekleme:** şifa, Reboot, hack ve Tech statüleri Tech'e ölçeklenir.
- **Silah ve temel saldırı:** envanter ve silahlar var; Act menüsünün ilk satırı silaha bağlı **Attack**.
- **Tur sonunda bakış yönü:** Wait'ten sonra yön seçilir.
- **Birim ilerlemesi:** XP ile karakter seviyesi, JP ile sınıf seviyesi (Faz 9). **Kalıcı HP yok** (bilerek ertelendi).
- **Overwatch (Mevcut):** Sniper yeteneği; bir sonraki turuna kadar menzile giren ilk düşmana tek atış (kaçınma geçerli), sonra söner.
- **Şarjlı yetenekler (Mevcut):** Piercing Beam (3 tik) ve Earthshaker (2 tik) hemen değil CT saatinde patlar; bekleyen alan turuncu, tur sırasında listelenir, kullanıcı düşerse boşa gider.
- **Hasar + statü (Mevcut):** Marked Shot ve Disarm hem vurur hem statü bırakır.

## Sınıf ve ilerleme

Sınıf sistemi FFT'nin job sisteminden uyarlandı: yetenekler JP ile satın alınır, sınıflar zincirle açılır, her birim iki sınıfı hibrit olarak kullanabilir. Ayrıntılar `TACTICA_SINIF_AGACI.md` dosyasında; burada özeti var.

| Kademe | Sınıflar |
| --- | --- |
| Başlangıç (5) | Cyber Knight, Blade, Deadeye, Netrunner, Field Medic |
| Orta (7) | Warden, Phantom, Sniper, Daemon, Surgeon, Duelist, Breacher |
| Üst (6) | Juggernaut, Reaper, Railgunner, Overmind, Cyber Saint, Warlord |

- **JP:** yalnızca etkili eylemlerle kazanılır; kazanılanın 1/4'ü kadroya paylaşılır. Sınıf seviyesi (0–8) o sınıfta kazanılan toplam JP'den gelir. Hız orta: ilk orta sınıf 3–4 kontratta açılır.
- **Kilitler:** orta sınıflar Lv2–3 ister, üst sınıflar iki ya da üç sınıfın birleşmesini (örn. Overmind: Daemon Lv4 + Sniper Lv3 + Surgeon Lv2).
- **Slotlar:** birincil sınıf, ikincil sınıf, 1 Reaksiyon, 1 Destek, 1 Hareket, 1 silah.
- **Hibrit kuralı:** iki üst düzey sınıf birlikte seçilemez. İkincil sınıf yalnızca aktif menüsünü getirir; istatistik, MP ve silah izni birincilden gelir.
- **Stat modeli:** durumsuz formül, `sınıf tabanı + büyüme × (seviye − 1)`. Karakter seviyesi yalnızca stat'ı ve JP bonusunu etkiler; yetenek, kilit ve slotlar sınıf seviyesine bağlıdır.
- **Özel karakterler:** normal sınıfları kullanır; hangi sınıf olacakları hikâye aşamasında belirlenecek.
- **Mastery:** bir sınıfı tamamen öğrenmek ödül verecek; ödülün ne olduğu açık soru.

### JP ve sınıf seviyesi (planlı)

JP, yalnızca etkisi olan eylemlerle kazanılır (isabet eden saldırı, başarılı hack, iyileştirme, diriltme, uygulanan buff/debuff); ıskalama ve bekleme JP vermez.

```latex
JP = \lfloor (8 + 2 \times \text{sınıf seviyesi} + \lfloor \text{karakter seviyesi} / 4 \rfloor) \times M \rfloor
```

`M` bir ayar değeridir; başlangıç değeri **3** (orta hız). Sınıf seviyesi, o sınıfta kazanılan toplam JP'den gelir; harcamak seviyeyi düşürmez. Eşikler FFT'ninkilerdir (ara değerleri kaynaklardan doğrulayamadım, ayarlanabilir):

| Seviye | 0 | 1 | 2 | 3 | 4 | 5 | 6 | 7 | 8 |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| Toplam JP | 0 | 100 | 200 | 350 | 550 | 800 | 1150 | 1550 | 2100 |

Tempo tahmini (oynanmadı): bir savaşta birim 3–4 etkili eylem yaparsa ≈100 JP; ilk orta sınıf (Lv3 = 350 JP) ≈3–4 savaşta açılır.

**Yetenek maliyetleri (JP):**

| Kademe | 1. aktif | 2. aktif | 3. aktif | 1. pasif | 2. pasif | Equip |
| --- | --- | --- | --- | --- | --- | --- |
| Başlangıç | 100 | 200 | 300 | 150 | 250 | 400 |
| Orta | 200 | 300 | 400 | 250 | 350 | 400 |
| Üst | 300 | 450 | 600 | 400 | 550 | 400 |

Bir sınıfı tamamen öğrenmek: başlangıç 1.000 JP (+400 Equip), orta 1.500, üst 2.300. Başlangıç kadrosu ve işe alınanlar, sınıflarının en ucuz aktifini hazır öğrenilmiş gelir.

### Sınıf kilitleri

| Sınıf | Kademe | Koşul |
| --- | --- | --- |
| Warden | Orta | Cyber Knight Lv3 |
| Phantom | Orta | Blade Lv3 |
| Sniper | Orta | Deadeye Lv3 |
| Daemon | Orta | Netrunner Lv3 |
| Surgeon | Orta | Field Medic Lv3 |
| Duelist | Orta | Blade Lv2 + Cyber Knight Lv2 |
| Breacher | Orta | Cyber Knight Lv2 + Deadeye Lv2 |
| Juggernaut | Üst | Warden Lv4 + Breacher Lv3 |
| Reaper | Üst | Phantom Lv4 + Duelist Lv3 |
| Railgunner | Üst | Sniper Lv4 + Breacher Lv3 |
| Cyber Saint | Üst | Surgeon Lv4 + Warden Lv3 |
| Overmind | Üst | Daemon Lv4 + Sniper Lv3 + Surgeon Lv2 |
| Warlord | Üst | Duelist Lv4 + Warden Lv3 + Phantom Lv2 |

### Hibrit kombinasyonlar

| Birincil \\ İkincil | Başlangıç | Orta | Üst |
| --- | --- | --- | --- |
| **Başlangıç** | Evet | Evet | Evet |
| **Orta** | Evet | Evet | Evet |
| **Üst** | Evet | Evet | Hayır |

İkincil sınıf yalnızca aktif menüsünü genişletir; istatistik, MP havuzu, silah izni ve Strength/Tech ölçekleme birincilden gelir. Silah gerektiren bir aktif, takılı silah uymuyorsa gri görünür; silahı açmak için Destek slotunda ilgili Equip yeteneği gerekir, yani Triage ya da Iron Hide gibi bir pasiften vazgeçersin.

Tüm sınıfların yetenek tabloları ve istatistikleri **Ek A** sekmesindedir.

**Koddaki kitler (Mevcut, 2026-10-25).** 18 sınıfın hepsi oynanabilir. Ek A repoda olmadığından eksik 12 sınıfın kitleri kodun bugün desteklediği mekaniklerle yeniden tasarlandı (ilk taslak, denge test edilmedi). Silah izni ve varsayılan silah sınıfla gelir; maliyetler yukarıdaki tabloya uyar.

| Sınıf | Kademe | Silah | Aktifler | Pasifler |
| --- | --- | --- | --- | --- |
| Deadeye | Başlangıç | Rail Rifle | Aimed Shot, Marked Shot (−Def/−Eva), Pinning Fire (alan, −Move/−Spd) | Return Fire (R, 4 kare), Steady Aim (+Str), Equip Rail Rifle |
| Sniper | Orta | Rail Rifle | Headshot, Long Shot (7 kare), Disabling Shot (stun) | Camouflage (+Eva), Vantage (M, +2 Jump) |
| Daemon | Orta | Cyberdeck | Hijack (güçlü Override), Corrupt (zamanla hasar), Black ICE (alan Tech hasarı) | Root Access (+3 Tech), Feedback Loop (R) |
| Surgeon | Orta | Injector | Mend Field (alan şifa), Nanite Drip (regen), Full Restore (güçlü Reboot) | Adrenal Surge (R, Brace), Combat Medic |
| Duelist | Orta | Monoblade, Shield Gauntlet | Lunge (2 kare), Disarm (−Str), Flurry | En Garde (R), Parry (+Eva) |
| Breacher | Orta | Launcher, Shield Gauntlet | Breach Charge (alan), Frag Volley (alan), Concussion (alan stun) | Blast Plating, Brace for Impact (R), Equip Launcher |
| Juggernaut | Üst | Shield Gauntlet, Launcher | Ram, Unstoppable (kendine buff), Earthshaker (2 kare alan) | Immovable (R), Heavy Frame |
| Reaper | Üst | Monoblade | Harvest, Death Mark (−Def/−Eva), Reap (alan) | Shadow Counter (R), Wraith Step (M, +2/+2) |
| Railgunner | Üst | Rail Rifle, Launcher | Rail Lance, Overcharge (+5 Str), Piercing Beam (alan, 6 kare) | Counter-Snipe (R, 5 kare), Stabilizers |
| Overmind | Üst | Cyberdeck | Mind Spike, Cascade (alan −Tech/−Spd), Mass Override (alan) | Neural Shield (R), Hive Mind (+4 Tech, +6 MP) |
| Cyber Saint | Üst | Injector, Shield Gauntlet | Sanctify (alan şifa), Aegis (alan buff), Resurrection (alan Reboot) | Grace (R), Halo |
| Warlord | Üst | Command Baton, Monoblade | Rally (alan buff), Command Strike, Break Ranks (alan debuff) | Iron Will (R), Commander, Equip Baton |

R = Reaksiyon, M = Hareket, diğerleri Destek. Hijack'in yaralı hedefe bonusu, Mass Override'ın boss'larda yarı şansı, Breacher'ın Friendly Fuse'u (alan yetenekleri dostlara dokunmaz) ve Sniper'ın Overwatch'u artık kodda (Mevcut).

## Hack ve işe alım

Hack, Tactica'yı FFT'den ayıran ana mekanik: **System Override** başarılı olursa düşman birimi taraf değiştirir ve savaşın geri kalanında oyuncunun birimi olur. Yetenek Netrunner'da (ve devamı Daemon, Overmind'da) bulunur.

- **Şans (Mevcut):** `Power + 3 × Tech − 4 × seviye farkı − direnç`, 5 ile 95 arasında sınırlı. System Override'ın Power'ı **9**, MP maliyeti **6**. Kingpin'i hack'lemek kontratı kazandırır (DefeatTarget).
- **Seviye cezası (Mevcut):** hedef hacker'dan yüksek seviyedeyse seviye başına −4.
- **Direnen hedefler (Mevcut):** Chrome Brute ve Jacknife'ta −30, Kingpin'de −20.
- **Frenler (Mevcut):** MP maliyeti, seviye cezası, boss direnci ve hack'lenen birimin CT'sinin 0'a düşmesi.
- **Ailesi (Mevcut):** Override → Hijack (Power 12, yarı canın altındaki hedefte +20%) → Mass Override (alan, boss'larda yarı şans, dostlara dokunmaz). Root Access ve Cascade gibi destek yetenekleri şansı ve MP'yi besler.
- **Formül kararı:** `Power 9 + 3 × Tech` seçildi (Faz 11); hack uzmanının Tech'i gerçekten fark yaratır.

Hack'lenen birimlerin sınıfı kilitlidir (FFT'nin canavarları gibi); JP kazanmaz ve slot kullanmaz. Savaş sonunda kalıcı olarak kadroya katılabilir (karar, Faz 11).

## Ekipman ve ekonomi

Silah, sınıfın elinde ne yapabileceğini belirler. Yedi silah türü var; bir türü kullanmak için birincil sınıfın izinli listesinde olmalı ya da Destek slotunda o türün Equip yeteneğini taşımalısın.

| Silah | Kimlik | Bonus alanı |
| --- | --- | --- |
| Shield Gauntlet | Ağır, savunmacı | Defense |
| Monoblade | Hızlı, hassas | Evasion, Speed |
| Rail Rifle | Menzilli | Menzil |
| Launcher | Alan hasarı | AoE hasarı |
| Cyberdeck | Hack ve Tech | Override şansı, Tech hasarı |
| Injector | Tıbbi | İyileştirme gücü |
| Command Baton | Komuta | Speed |

- **Kademeler:** Mk I (başlangıç), Mk II (+2 Power), Mk III (+4 Power) ve adı olan eşsiz silahlar (+3 ile +5, tek bir özel etki).
- **Edinme: loot + shop.** Loot, savaş ve ilan ödülü ile düşman düşürmesidir; eşsiz silahlar yalnızca buradan çıkar. Shop mahallelerde (her mahallenin kendi stoku var): Mk I ve Mk II serbest, Mk III kampanya ilerledikçe stoka girer.
- **Para birimi (Mevcut):** kredi (cr). Kaynak: kontrat ve hikâye ödülleri (yol savaşı yarısını öder) ve silah satışı (fiyatın yarısı). Yeni oyun 300 cr ile başlar; eleman kiralamak seviye × 80 cr. Fiyat ölçekleme test edilmedi.
- **Bugün (Mevcut):** Mk I–III silahlar (Mk III: Mk I + 4 Power, fiyat Mk II × 1,8). Her mahallenin kendi stoğu var; temizlenmiş mahalle fazladan stok açar (Neon Row Mk II, Rustyard ve Spire Gate Mk III). Eşsiz silahlar Planlı (nereden düşeceği açık soru).

## Dünya haritası ve hikâye

Oyun, şehrin mahallelerinden oluşan bir harita üzerinde ilerler (FFT'nin dünya haritası gibi). Mahalleler hikâye ilerledikçe açılır. Harita aynı zamanda ana menüdür: crew, sınıf ve slot düzenleme, shop, işe alım ve ilan panosuna haritadan ulaşılır; ayrı bir güvenli üs yoktur. Yalnızca savaş sırasında crew menüsü kapalıdır.

**Mahalle (district)** şu özelliklere sahiptir:

| Özellik | Etkisi |
| --- | --- |
| Hizmetler | Shop, işe alım havuzu, ilan panosu; her mahallenin kendi stoku ve havuzu var |
| Tehlike seviyesi | Yol savaşlarındaki düşman seviyesi |
| Hâkim fraksiyon | Düşman tipleri (çete, corp, drone) |
| Bağlantılar | Hangi mahalleye yol var; iki mahalle arasında güvenli yol ve kısayol olabilir |
| Durum | Hikâyeyle "temizlenince" yol savaşları azalır, shop stoku genişler |

**Üç savaş türü:**

| Tür | Nasıl başlar | Tekrar | Ödül |
| --- | --- | --- | --- |
| Hikâye savaşı | Hikâye adımı | Bir kez | Hikâye ilerler, mahalle açılır, eşsiz loot |
| Yol savaşı (encounter) | Seyahatte yolun kesilmesi | Tekrarlanır | JP, loot, para |
| İlan | Panodan seçilir | Pano zamanla yenilenir | Para, loot |

Kurallar:

- **Hiçbir yer güvenli değil.** Shop'a gitmek için yolda savaş çıkabilir. Şans mahallenin tehlikesine bağlı (her yolda değil); savaştan sonra kısa bir koruma süresi olur ve ilk ziyaret hikâyeyle sabittir.
- **Yol savaşları çeşitli olur:** çoğunlukla "hepsini yen", ara sıra pusu (N tur hayatta kal), koruma ya da bir hedefi hack'leme.
- **Savaş öncesi ekran:** 8 kişiden 4'ünü seç, düşman önizlemesini gör; yol savaşlarında da çıkar.
- **Kayıt:** haritada her yerde elle, her savaştan sonra otomatik.
- **Mevcut 6 kontrat:** Chrome Brute ve Cut the Signal hikâye savaşlarına; Street Sweep, Gang Turf, Block Watch ve Courier Run ilanlara dönüşür (Mevcut, Faz 14; Data Heist Spire Gate'in ilanı, Velvet Room Raid yeni açılış savaşı). Düşman karışımları sınıf ve düşman tasarım dosyasında.
- **Hikâye** savaş öncesi ve sonrası metin kutularıyla başlar; özel karakterler hikâyede ekibe katılır. Hikâye henüz yazılmadı.

Başlangıç adımı olarak 3 mahalle, 1 kısa hikâye hattı ve görselsiz bir düğüm ya da liste haritası öneriyorum.

**Bugün kodda (Mevcut, Faz 14):** üç mahalle, liste haritası: **Neon Row** (açılış, Velvet Room Raid → Yuki katılır, Rustyard açılır), **Rustyard** (Chrome Brute → Spire Gate açılır) ve **Spire Gate** (Cut the Signal, Data Heist). Yol savaşı şansı tehlike × %15 (en fazla %75); savaş öncesi brifing ekranı (deploy seçimi, düşman önizlemesi, ödül) ve hikâye metinleri var. Kayıt haritada elle, her savaştan sonra otomatik.
**Şehir haritası kuralları (Mevcut):**
- **Yol savaşları ve üretilen ilanlar** crew'un seviyesine ve mahallenin tehlikesine göre ölçeklenir: düşman seviyesi `max(tehlike, crew seviyesi + tehlike − 3)` (Neon Row crew'un iki altı, Rustyard eşit, Spire Gate bir üstü), sayı tehlikeyle artar, 6. seviyeden sonra mahallenin elit havuzundan tam kitli crew job'ları katılır, düşmanlar mahallenin kendi havuzundan seçilir, hedef yenme / lideri düşürme / koruma / **"N tur dayan"** arasında değişir.
- **İlk ziyaret** asla pusuya düşmez; **güvenli yollar** (mahalle tanımında) da öyle.
- **Savaş sonrası** bir yolculuk güvenlidir ("lying low").
- **Temizlenmiş mahalle** (kontratları bitmiş): pusu şansı yarıya iner, shop fazladan stok açar; haritada CLEARED yazar.
- **İlan panosu** her görevden sonra yenilenir: elle yazılmış ilanların yanında mahalleye göre üretilen 2 kontrat (seed'li, aynı panoda aynı ilanlar).
- Travel ekranı her yolun pusu şansını ve nedenini gösterir.

## Hikâye ve karakterler (öneri / taslak)

Bir şirket polisinden ihraç edilen eski polis (ana karakter), ihracının arkasında hem içeriden hem dışarıdan kimin olduğunu bulmaya çalışır; ekibi yolda katılan karakterlerle büyür. Temalar: şirketlerin polis güçlerini yönetmesi ve sistem eleştirisi, dışlanma, zihin kontrolü. Hikâyenin sonu henüz belirlenmedi. Çalışma taslağı ayrı doc'ta (Tactica — Hikâye Taslağı); burası özetidir.

| Karakter | Rol | Sınıf | Katılma | Görev dizisi |
| --- | --- | --- | --- | --- |
| Ana karakter (isimsiz) | Eski polis, görevden ihraç edildi | Cyber Knight | Baştan | Ana hat: ihracın arkasındakiler |
| Rex | Ana karakterin yakın arkadaşı; rahat, "takılan" biri; Afrika kökenli | Netrunner | Baştan | Açık |
| Yuki | Strip club'ta dansçı; mekândaki mobbing yüzünden patronuna saldırır | Deadeye | Açılış baskını | Gerçek Jacknife'ı bulmak |
| Seraph | Erkek netrunner; mind hack mağduru | Açık | Açık | Yan zincir; dışlanırsa ekipten ayrılabilir |

- **Açılış:** Ekip bir strip club'a baskın yapar; amacı mekân sahibini sorguya çekmek. Yuki mekândaki mobbingden bunalıp patronun odasına gider ve ekip odaya girerken adamı öldürür; Yuki ekibe katılmak ister, ekip onu gerçek Jacknife'ı bulma sözüyle davet eder. Oyun tarafı: ilk hikâye savaşı, öğretici; hedef birim savaşın içinde senaryo gereği ölür; Yuki önce guest, sonra kalıcı olur.
- **Jacknife:** Mekân sahibi Jacknife adıyla tanınıyor ama bu bir takma isim; farklı yerlerde farklı insanlar Jacknife adında birini tanıyor. Ekibin hedefi sanıldığı kişi değil. Öneri: her mahallenin kendi "Jacknife"ı olsun (söylenti ilanı → hikâye savaşı); boss'lar sahte Jacknife olabilir; sonunda Jacknife'ın bir kişi, bir ağ ya da şirketlerin uydurduğu bir isim olduğu ortaya çıkar.
- **Seraph:** Mind hack ile kontrol edilen erkek arkadaşı kendisini öldürmeye çalışınca nefsi müdafaa ile onu öldürdü ve o hacker'ın kim olduğunu bulmak istiyor. Eşcinsel bir karakter; hikâye dışlanma temasını onun üzerinden işler. Bu hat ana hikâyeyi etkilemez. Ekipte dışlanıldığını ve kimsenin ona yardım etmediğini fark ederse ekipten çıkabilir.
- **Prototipteki hâli (Mevcut):** ekip Aria (Cyber Knight), Rex ve Bram ile başlar; Velvet Room Raid'de Yuki (**Deadeye**, Rail Rifle) misafir olarak savaşır ve hedef Jacknife'ı düşürünce kalıcı olarak katılır. Ana karakter ve açılış sahnesi tasarımdaki hâline getirilmedi.
- **Görev dizileri:** Her özel karakterin kendi görev dizisi olur; işe alım havuzundan gelen birimlerin yoktur. Öneri: dizi bitince imza yetenek verilebilir.
- **Override ve hikâye:** Seraph mind hack mağduru, oyuncunun en güçlü aracı ise Override. Öneri A: aynı teknoloji, oyuncu ikilemin içinde ve Seraph tepki verir. Öneri B: Override yalnızca cyberware'e işler, mind hack ayrı ve yasaktır. Karar açık.

## Düşmanlar

Sekiz düşman tipi ve iki boss tasarlandı. Her biri oyuncuya bir ders ve bir hedef verir; hepsi kendi job'unda sabit yeteneklerle gelir.

> **Tablodaki dokuz tip de kodda (Mevcut, Faz 12–13)** ve değerler bu tabloyla aynı.

| Tip | Taraf | Rol | HP | Str | Def | Spd | Yetenek |
| --- | --- | --- | --- | --- | --- | --- | --- |
| Gooner | Çete | Hızlı yem | 15 | 3 | 1 | 9 | Goon Punch |
| Raider | Çete | Bruiser | 20 | 5 | 3 | 5 | Rebar Swing |
| Slinger | Çete | Menzilli, cam | 12 | 4 | 1 | 7 | Scrap Shot (menzil 3) |
| Patch Rat | Çete | Düşman medik | 14 | 2 | 2 | 6 | Patch, Reboot |
| Packet Thief | Çete | Düşman hacker | 12 | 2 | 1 | 7 | Ping, Lag Spike |
| Corp Trooper | Corp | Dengeli asker | 20 | 6 | 4 | 6 | Burst Fire (menzil 3) |
| Sec-Drone | Neutral | Süzülen, çevik | 10 | 5 | 2 | 10 | Zap (menzil 2) |
| **Chrome Brute** (boss) | Çete | Yavaş, sert | 36 | 8 | 6 | 4 | Piston Slam (alan 1) |
| **Kingpin** (boss) | Çete | Komutan | 28 | 6 | 4 | 6 | Chrome Fist, Reboot, Command Link |

- **Sec-Drone** Neutral faction'ın ilk gerçek kullanımı: hem oyuncuya hem çetelere saldırır.
- **Packet Thief** debuff kullanan AI gerektirir (aşağıda yol haritasında).
- **Chrome Brute** yavaş (Spd 4) ve Evasion 0: arkadan vuruş zaten garantili, Override'a dirençli (−30).
- **Kingpin** hedef birimdir; düşürmek ya da hack'lemek kontratı bitirir.

### Düşman AI'ı

**Mevcut (Faz 13): puanlı karar.** AI bu tur ulaşabildiği her (yetenek × hedef × duracağı kare) seçeneğini puanlar ve en yükseğini seçer:

- Beklenen hasar × isabet şansı, öldürme bonusu, hedefin rolü (medik ve hacker önce).
- Alan yeteneklerinde dost ateşi cezası.
- %60 HP altındaki müttefiki iyileştirme, bayılmış müttefiki Reboot.
- Süresi dolmamış buff'ı tekrar atmama; düşmana stun, zamanla hasar ve debuff.
- Override şansı (AI'da mevcut, ama hiçbir düşman job'unda Override yok).
- Şarjlı yetenekler daha düşük puanlanır (bekleme riski); Overwatch kurmak küçük bir puan alır.
- **Geri çekilme:** %30 HP altında ve iyi bir seçeneği yoksa saldırganlardan uzaklaşır.
- **Overwatch'tan kaçınma:** izlenen karelere ancak başka yol yoksa girer.
- Taunt: alay eden birime ulaşabiliyorsa zararlı seçenekleri ona kilitlenir.

Hiçbir şeye ulaşamıyorsa ana saldırısıyla yürüme mesafesi en yakın düşmana yürür.

## Arayüz ve ekranlar

| Ekran | Amaç | Durum |
| --- | --- | --- |
| Ana menü | New Game, Continue, Quit | Mevcut |
| Harita | Dünya haritası ve ana menü: mahalleler, crew, shop, işe alım, ilanlar | Mevcut (liste haritası; mahalle ekranı hub) |
| Crew | Kadro: Deployed/Reserve, Manage, Dismiss (onaylı) | Mevcut |
| Birim ekranı (Manage) | Karakter kartı + Jobs (kademe sütunlu iş ağacı) / Equipment / Learn sekmeleri; kaydırmasız | Mevcut |
| Hire Runners | 3 adaylık havuz, mahalleye bağlı, ücretli | Mevcut |
| İlan panosu | Mahallenin kontratları | Mevcut (zamanla yenilenmiyor) |
| Shop | Silah alım-satımı, kimin kullanabileceği | Mevcut |
| Savaş öncesi ekran | Brifing: hikâye metni, hedef, rakipler, ödül, deploy seçimi | Mevcut |
| Yerleştirme | Ekibi başlangıç bölgesine dizme | Mevcut |
| Savaş HUD | Act/Move/Wait (1/2/3), HP/MP barı, Turn Queue, hedef önizlemesi, faction halkaları, uçan yazılar | Mevcut |
| Sonuç ekranı | JP, XP, kredi, loot, hikâye sonucu, katılan karakter | Mevcut |
| Hikâye diyaloğu | Savaş öncesi/sonrası metin | Mevcut (brifing ve sonuç ekranında düz metin) |

Kadro üst sınırı 8, aynı anda göreve çıkan 4. Crew menüsü haritada her yerden açılır; yalnızca savaş sırasında kapalıdır. Kayıt haritada her yerde elle, her savaştan sonra otomatik (öneri).

## Zorluk ve denge notları

İlk sayılar kâğıt üstünde hesaplandı. 2026-10-26 denge geçişi otomatik savaşlarla ölçüldü (sonuçlar PROGRESS.md'de): düşmanlar seviyeyle crew kadar büyür ve seviyelerine göre silah taşır, üretilen işler crew'un seviyesini izler, 6. seviyeden sonra düşman takımına tam kitli crew job'ları (elitler) katılır.

- **Hasar ölçeği:** `max(1, Power + Strength − Defense, Power / 4)` (denge geçişi, 2026-10-26: önceden zırhlı birimler her şeyden 1 alıp ölümsüzleşiyordu). Örnek: Lv2 Cyber Knight (Str 10) Chrome Brute'a 9 vurur (4 vuruş); Brute ona `7 + 8 − 5 = 10` vurur (3 vuruş). Brute'un Speed'i 4 olduğu için oyuncu neredeyse iki tura karşı bir oynar.
- **Override şansı:** `Power + Tech` Tech 2 ile Tech 8 arasında neredeyse aynı şansı veriyor (kodda Power 35 ile %37 / %43). Öneri: `Power 9 + 3 × Tech` (Tech 8 → %33, Tech 2 → %15, Overmind Tech 14 → %51 + Root Access).
- **Override ailesi** (Netrunner, Daemon, Overmind) üst düzeyde %60–70'e çıkabilir. Frenler: seviye farkı cezası, boss dirençleri, MP maliyeti 6–12, hack'lenen birimin ilk turda CT 0 ile başlaması.
- **Evrensel ikincil riski:** herkes Medic/Surgeon aktiflerini ikincil alırsa o aile gereksizleşir. Frenler: MP birincilden gelir, pasifler gelmez, şifa Tech'e ölçeklenir, tek Destek slotu.
- **Destek slotu kıt:** Equip yetenekleri pasiflerle yarışır; FFT'nin de tasarım niyeti bu. Slot sayısını artırmak hibrit gücünü çok şişirir.
- **AoE dostlara vurur** (Shockwave); Breacher'ın Friendly Fuse'u bu yüzden gerçek bir seçimdir.
- **Counter yetenekleri** aynı olay kancasını kullanır; bir karşılığa karşılık verilmez (sonsuz döngü yok).
- **Silah Power'ı:** Mk III (+4) iki seviye Strength'e denk; ekonomi gelmeden daha yüksek tutma.
- **JP hızı** (M = 3) bir tahmin; ilk oturumdan sonra ilk orta sınıfın kaç savaş sonra açıldığına bakılıp ayarlanmalı; yol savaşları JP kaynağı olduğunda yeniden hesaplanmalı.
- **Yol savaşları + flatline:** 3 turda kurtarılmayan birim kalıcı ölür; kaçınması zor bir savaşta bu sert olur. Geri çekilme seçeneği (`AbortCombat` zaten var) ya da yol savaşlarında 5 turluk sayıcı düşünülebilir.

## Teknik mimari özeti

Unity 6 (`6000.6.7f1`, URP), 59 script / yaklaşık 12.900 satır C# (2026-10-25 çalışma kopyası). Girdi yalnızca Input System paketidir; eski `UnityEngine.Input` çalışma anında hata verir. Tek sahne (`SampleScene`).

- **Namespace'ler:** `Tactica.Grid`, `Camera`, `Stats`, `Combat`, `Campaign`, `UI`; bağımlılık grafiği döngüsel değil ve parçalar olaylarla ayrılıyor. Combat, Grid'i yönetir; Grid "sıra kimde" diye sormaz.
- **Izgara:** `Dictionary<Vector2Int, GridTile>`; harita seed'e bağlı 5 geçişli `MapGenerator` ile üretilir (aynı seed aynı harita).
- **Kurallar:** `ActionResolver` düz C# sınıfı (MonoBehaviour değil), sahneden bağımsız ve testte oluşturulabilir; HUD önizlemesi aynı formülleri okur.
- **Veri:** `JobDefinition`, `AbilityDefinition` ve `CampaignConfig` ScriptableObject; `CharacterStats` (taban) ile `CurrentStats` (anlık) ayrıdır.
- **Kayıt:** `campaign.json`, sürüm 3. Eski sürümler yüklenirken yükseltilir (geçersiz sayılmaz).
- **Fraksiyon:** `Faction` (Player/Enemy/Neutral) ile kontrol (`IsPlayerControlled`) ayrı; guest oyuncu tarafında ama AI kontrolünde.

**Bu tasarım için gereken yeni veri ve sistemler** (tablodaki ilk sekiz satır Faz 8–14'te yapıldı; Hikâye satırındaki görev günlüğü hâlâ yok):

| Parça | Değişiklik | Zorluk |
| --- | --- | --- |
| `AbilityDefinition` | `jpCost`, slot türü, silah gereksinimi | Küçük |
| `JobDefinition` | Kademe, ön koşullar, izinli silahlar, yetenek listesi | Küçük |
| Birim kaydı (`RosterUnit`) | Sınıf başına JP, öğrenilen yetenekler, slot seçimleri, silah, kilit bayrağı | Orta |
| `CampaignState` | JP kazanımı ve paylaşımı, kilit kontrolü, harita konumu, hikâye bayrakları | Orta |
| `DistrictDefinition` | Mahalle: hizmetler, tehlike, fraksiyon, bağlantılar | Orta |
| `EquipmentDefinition` | Silah türü, kademe, Power bonusu, bonus alanı | Orta |
| Savaş | AoE, durum etkileri, reaksiyonlar, temel saldırı | Orta |
| AI | Debuff/hack kullanan düşman, Taunt, Overwatch tepkisi | Büyük |
| Hikâye | Diyalog kutuları, hikâye bayrakları, görev günlüğü | Büyük |

Yetenekler uygulama zorluğuna göre etiketlendi (T0 = mevcut kodla çalışır, T1 = birkaç satır, T2 = AoE/buff/reaksiyon/ekipman, T3 = AI, ışınlanma, charge); etiketler Ek A'dadır.

## Sanat ve ses yönü (öneri, karar yok)

Bu alanda henüz bir karar verilmedi; arayüz bilerek çıplak. Birimler geçici olarak Unity'nin Adam Character Pack modellerini kullanıyor (Mevcut): Cyber Knight → Adam, Gooner → Guard, Kingpin → Lu, Chrome Brute → büyütülmüş Guard, Courier ve Civilian → Crowd; paket yoksa kapsül + yön vizörü. Bu paket yalnızca kişisel, ticari olmayan kullanıma izinli ve dağıtılamaz: oyun satılacaksa yerine başka modeller gerekir.

- **Ton:** neon siberpunk şehir; çete mahalleleri ile corp bölgeleri görsel olarak ayrışsın (renk paleti, mimari).
- **Birimler:** sınıf başına renk/siluet ile başlamak yeterli; model sonra. Fraksiyon ızgarada da görünüyor (Mevcut: ayak altında renkli halka; misafir yeşil, hack'lenen mor).
- **Geri bildirim (Mevcut, ilk geçiş):** uçan hasar/iyileşme sayıları, KO, REBOOT, MISS, RESISTED, HACKED ve statü yazıları; kareler arası zıplama, vuruşta sarsılma, KO'da düşme; sentetik geçici sesler. Planlı: ikonlar, gerçek sesler, yürüme animasyonu.
- **Ses:** müzik ve efekt yönü belirlenmedi.

## Yol haritası ve açık sorular

Fazların güncel listesi ve her fazın kapsamı [PROGRESS.md](PROGRESS.md)'dedir; aşağıdaki sıra onun kaynağıdır. **Faz 0–14 tamam, Faz 15'in (cila) ilk geçişi bitti**; aşağıdaki yedi adımın hepsi koda girdi. Sıradaki işler PROGRESS.md'nin "Next up" bölümünde (eksik sınıflar, testler, harita derinliği, hikâye). Bu tasarımın koda geçmesi için önerilen (ve izlenen) sıra:

1. **Yetenek altyapısı:** `AbilityDefinition`'a `jpCost`, slot türü ve silah gereksinimi; `JobDefinition`'a kademe, ön koşullar ve izinli silahlar (küçük değişiklikler).
2. **JP ve kayıt:** sınıf başına JP, öğrenilen yetenekler, slot seçimleri; `campaign.json` biçimi değişir ve eski kayıtlar geçersiz olur.
3. **Crew ekranı:** sınıf ve slot düzenleme, yetenek satın alma, silah seçimi, hibrit kuralı doğrulaması.
4. **İlk dikey dilim:** Knight, Blade, Netrunner, Medic + Warden ve Phantom (karar sonra).
5. **Orta kod işleri:** AoE, buff/debuff süresi, reaksiyonlar, ekipman verisi. *(PROGRESS.md'de bu adım dikey dilimden (4) önceye alındı: dilimdeki yeteneklerin çoğu bunlara bağlı.)*
6. **AI:** debuff ve hack kullanan düşman, Taunt, Overwatch gibi etkileşimler.
7. **Harita, mahalleler ve shop** (3 mahalle, 1 kısa hikâye hattı, görselsiz düğüm haritası; para birimi kararı gerekir), sonra Faz 8 cilası.

### İlk dikey dilim (öneri)

Bu sırayla ilerlemek için küçük ama tam çalışan bir parça:

- **Harita:** 3 mahalle, görselsiz düğüm ya da liste haritası.
- **Hikâye:** açılış baskını (Yuki'nin katılışı) ve Jacknife hattının ilk ipucu; metin kutuları.
- **Sınıflar:** Cyber Knight, Blade, Netrunner, Field Medic + Warden ve Phantom (6 sınıf); üst sınıflar sistem oturunca.
- **Sistemler:** JP, slotlar, hibrit, Equip; silahlar Mk I–II; basit shop; yol savaşları.
- **Düşmanlar:** Gooner, Raider, Slinger, Patch Rat; boss olarak Chrome Brute.

### Riskler

- **Kapsam şişmesi:** harita, hikâye, sınıf sistemi, shop ve AI birlikte büyük bir iş; dikey dilimle sınırla.
- ~~**Eski kayıtlar:** `campaign.json` biçimi değişecek ve eski kayıtlar geçersiz olacak.~~ Çözüldü: eski kayıtlar yükseltiliyor.
- **Yetenekler koda bağlı:** tasarlanan yeteneklerin çoğu durum etkisi, AoE, reaksiyon ya da AI istiyor (T2/T3); önce altyapı, sonra yetenek.
- **Tempo tahminleri test edilmedi:** JP hızı, shop fiyatları ve kontrat ödülleri tahmindir.
- **Yol savaşları + kalıcı ölüm:** oyuncuyu cezalandırırsa sıkıcı olur; geri çekilme ya da daha uzun sayıcı düşünülür.
- **Seraph'ın ayrılması:** oyuncunun yatırımını (JP, ekipman) boşa çıkarabilir; uyarı, iade ya da önlenebilirlik gerekir.
- **Hikâye yazılmadı:** son ve başlıca çatışma (içeriden/dışarıdan taraflar) belirsiz; harita ve yan görevler buna bağlı.

**Açık sorular**

- ~~**Override formülü**~~ → `Power 9 + 3 × Tech` (Faz 11).
- ~~**Hack'lenen birim kalıcı mı?**~~ → Evet, kilitli sınıfla kadroya katılabilir (Faz 11).
- **Mastery ödülü:** gizli sınıf mı, kalıcı stat bonusu mu, ikisi de mi?
- ~~**Reaksiyon tetik oranı**~~ → yeteneğe özel sabit yüzde (Faz 11).
- ~~**Para birimi**~~ → ikisi de: kontrat ödülü ve silah satışı (Faz 14). Fiyat ölçekleme hâlâ test edilmedi.
- **Eşsiz silahlar:** hangi boss ya da kontrattan düşsün?
- **Düşmanlar JP ve slot kullansın mı?** Şimdilik sabit.
- **Kayıt politikası:** haritada elle kayıt + savaş sonrası otomatik kayıt mı, tek ironman dosyası mı?
- **Hikâye ve özel karakterler:** Seraph'ın başlangıç sınıfı açık (diğerleri belli); diğer hikâye soruları Hikâye Taslağı doc'unda. Yol savaşlarında geri çekilme ya da 5 turluk flatline olsun mu? Yol savaşları JP kaynağı olunca JP hızı (M = 3) yeniden ayarlanmalı.

Ayrıntı dosyaları (sınıf tabloları artık Ek A sekmesinde, hikâye için Hikâye Taslağı doc'u): `TACTICA_SINIF_AGACI.md` (sınıflar), `TACTICA_SINIF_VE_DUSMAN_TASARIMI.md` (düşmanlar), `FFT_JOB_SISTEMI_NOTLARI.md` (FFT araştırması).

## Sözlük

| Terim | Anlamı |
| --- | --- |
| CT (charge time) | Her birimin Speed kadar biriktirdiği sayaç; 100'e ulaşınca sıra onda |
| JP | Job Points: etkili eylemle kazanılır, yetenek satın almaya ve sınıf seviyesine gider |
| Sınıf seviyesi | O sınıfta kazanılan toplam JP'den gelir (0–8); yetenek ve kilitleri belirler |
| Karakter seviyesi | XP ile gelir; stat'ları ve JP bonusunu etkiler |
| Tech | Hack ve tıbbi becerilerin stat'ı (koddaki adı şimdilik `Magic`) |
| Override | Netrunner'ın düşmanı taraf değiştirten hack'i (`System Override`) |
| Reboot | Bayılmış müttefiki ayaklanıdır (FFT'nin Raise'i) |
| Flatline | Bayılan birimin 3 tur içinde kurtarılmazsa kalıcı ölmesi |
| Guest | Oyuncu tarafında ama AI kontrolündeki birim |
| Faction | Player, Enemy ya da Neutral; kimin kime vurabileceğini belirler |
| Mahalle (district) | Haritadaki bölge; kendi hizmetleri, tehlikesi ve fraksiyonu var |
| Yol savaşı (encounter) | Seyahatte yolun kesilmesiyle başlayan savaş |
| İlan | Panodan seçilen, isteğe bağlı hedefli iş |
| Mk I–III | Silah kademeleri (+0, +2, +4 Power) |
| Equip | Bir silah türünü Destek slotunda açan yetenek |
| Mastery | Bir sınıfın tüm yeteneklerini öğrenmek; ödülü açık |
