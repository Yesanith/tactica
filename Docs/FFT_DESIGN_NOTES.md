# Final Fantasy Tactics — Oyun Tasarımı Notları

> Tactica için referans notları. Amaç FFT'yi kopyalamak değil; **her sistemin neden işe yaradığını**
> anlamak ve hangisinin Tactica'ya, nasıl uyacağına karar verebilmek.
>
> Sayılar, aksi belirtilmedikçe Aerostar'ın *Battle Mechanics Guide* v6.5'inden (BMG, NA PS1 sürümü)
> alındı. BMG, oyunun kodundan çıkarılmış en güvenilir kaynak. Remaster/WotL sürümlerinde bazı
> değerler farklı olabilir. Doğrulanamayan bilgiler **(doğrulanmadı)** diye işaretli.

Son güncelleme: 2026-10-24.

---

## 1. Genel çerçeve

- **Yönetmen:** Yasumi Matsuno (Tactics Ogre, Ogre Battle). FFT, Tactics Ogre'nin sistemlerini
  sadeleştirip Final Fantasy'nin job sistemiyle birleştirir.
- **Görsel fikir:** İzometrik, döndürülebilir bir **diorama**. Harita bir masa üstü maket gibi
  sunulur; yükseklik, oyunun okunabilir bir parçasıdır.
- **Döngü:** Dünya haritasında düğümler (şehir/savaş alanı) → hikâye savaşları + rastgele
  karşılaşmalar (grind) → şehirde alışveriş, job değiştirme, kadro yönetimi → tekrar.
- **Savaşa giren birim sayısı:** Çoğu savaşta en fazla **5 birim** (doğrulanmadı; genel bilgi).
  Kadro (roster) PS1'de 16 kişi (doğrulanmadı).

**Tasarım dersi:** Savaş başına az birim + büyük kadro = "kimi götüreceğim?" sorusu her
savaştan önce anlamlı bir karar olur.

---

## 2. Zaman: CT (Charge Time) sistemi

FFT'nin kalbi. Sabit bir "round" yok; herkesin kendi saati var.

| Kural | Değer (BMG 1.1) |
|---|---|
| Her clocktick'te | `CT += Speed` |
| Tur alma | `CT >= 100` olan birim **Active Turn (AT)** alır |
| Tur sonunda: hem hareket hem aksiyon | `CT -= 100` |
| Sadece hareket **veya** sadece aksiyon | `CT -= 80` |
| Hiçbiri (sadece Wait) | `CT -= 60` |
| Üst sınır | Kalan CT **60'tan büyükse 60'a çekilir** |

- **Speed sırayı değil sıklığı belirler.** Hızlı birim savaş boyunca daha çok tur alır.
- **Taşan CT kaybolmaz** (60 sınırına kadar). Speed 11 olan birim, 10 olanı zamanla geçer.
- **Charge (yavaş) aksiyonlar:** Büyüler hemen çıkmaz; **CTR** (clockticks till resolution)
  kadar sonra çözülür. Örnek: Charge +1 → 4 tick, +5 → 10, +20 → 35. Jump: `CTR = 50 / Speed`.
  Bu sürede hedef kaçabilir, büyücü öldürülebilir. Hareket eden büyücünün charge'ı iptal olur.
- **Haste / Slow / Stop / Sleep** doğrudan saat üzerinde oynar. Sleep alan birimin CT'si artmaz.

**Neden işe yarıyor:**
1. **Tempo bir kaynak.** Hareket etmemek ya da saldırmamak bedava değil ama ucuz: sonraki tur daha
   erken gelir. "Bu tur sadece pozisyon alayım" gerçek bir seçenek olur.
2. **Telgraflanmış tehdit.** Charge süresi, rakibin ne yapacağını önceden görüp karşılık vermeyi
   sağlar. Büyü hedefinden kaçmak veya büyücüyü kesmek taktiksel bir oyundur.
3. **Hız statı her şeyi etkiler.** Bu yüzden Speed'i artıran şeyler (Haste, ekipman) en değerli
   şeylerdir; denge açısından risklidir.

> **Tactica'da şu an:** Savaş başında Speed'e göre sabit bir sıra (`BuildInitiativeOrder`) var ve
> round mantığı kullanılıyor. `CombatManager` yorumu, CT sistemine geçişin "sadece bu metodu
> değiştirmek" olacağını zaten not etmiş.
>
> **Öneri (yüksek öncelik):** CT'ye geç. 100/80/60 bedeli ve 60 üst sınırı doğrudan alınabilir.
> Tactica'nın `Wait` kuralı (saldırmadıysan savunma duruşu) CT ile çakışır; Wait'in ödülü ya CT
> iadesi ya da savunma olmalı, ikisi birden değil. HUD'a sıradaki 8–10 turu gösteren bir **turn
> order şeridi** eklenmeli; CT sistemi görünmezse okunmaz.

---

## 3. Tur içi eylem ekonomisi

- Her AT'de **bir kez hareket** ve **bir kez aksiyon** (her iki sırada da olur).
- Hareket ettikten sonra aksiyon menüsüne geçilebilir; hareket geri alınamaz.
- Tur **bakış yönü seçilerek** biter (bkz. §4).

> **Tactica'da şu an:** Hareket tur başına bir kez, ama **saldırı sınırsız** (belgelenmiş bilinen
> açık). **Öneri:** Aksiyonu da bir kez yap. FFT'nin bütün dengesi "bir hareket + bir aksiyon"
> varsayımına dayanıyor; sınırsız saldırı Speed ve CT'nin anlamını yok eder.

---

## 4. Konum: bakış yönü (facing) ve kaçınma (evasion)

Her birimin bir **yönü** var. Saldırının geldiği yön, hangi kaçınma değerlerinin uygulanacağını
belirler (BMG 1.4):

| Saldırı yönü | Uygulanan kaçınmalar |
|---|---|
| **Önden** | Class Ev × Shield Ev × Accessory Ev × (Weapon Guard) |
| **Yandan** | Shield Ev × Accessory Ev (class evasion **yok**) |
| **Arkadan** | Sadece Accessory Ev |
| **Büyü (her yönden)** | Magic Shield Ev × Magic Accessory Ev |

```
hit% = base% × (100−CEv) × (100−SEv) × (100−AEv) × (100−WEv) / 10^8     (önden)
```

- Yön, ileri/yan/geri mesafelerden hangisi en büyükse o kabul edilir. Eşitlikte daha "önde" olan
  kazanır. Yükseklik yön hesabına girmez.
- **Defending** durumu ya da **Abandon** reaksiyonu tüm kaçınma yüzdelerini **ikiye katlar**.
- Zodiac kaçınmayı etkilemez.

**Neden işe yarıyor:** Kuşatma ve arkadan vurma doğal olarak ödüllendirilir. Tur sonunda yön
seçmek de her turu küçük bir savunma kararıyla bitirir. Ayrı bir "flanking bonusu" sistemine
gerek kalmadan konum önem kazanır.

> **Tactica'da şu an:** Yön yok, kaçınma yok, her saldırı isabet ediyor.
> **Öneri (orta öncelik):** `GridUnit`'e `Facing` (4 yön) ekle. Wait/tur sonu yön seçtirsin.
> Önce basit bir isabet şansı (`baseHit × (1 − evasion[yön])`) ile başla.

---

## 5. Arazi: hareket, yükseklik, Jump

- FFT hareketi **4 yönlüdür**; çapraz adım yok. Menzil ve hareket alanları **elmas** şeklindedir
  (Manhattan mesafesi).
- Her job'un **Move** (kaç kare) ve **Jump** (bir adımda tırmanılabilecek yükseklik) değeri var.
  Örnek: Knight `Move 3 / Jump 3`.
- Yükseklik çoğu yeteneğin menzilini etkilemez. Ama:
  - **Longbow menzili:** `5 + [(atanın yüksekliği − hedefin yüksekliği) / 2]`. Yüksekten ok daha
    uzağa gider.
  - Bazı yeteneklerin dikey sınırı var (`menzil v dikey`, örn. Threaten `3v2`).
- Yüksek zemin özellikle menzilli birimler için çok değerli.

> **Tactica'da şu an:** 8 yönlü hareket var, "yol başına tek çapraz" kuralıyla. Menzil ise
> Chebyshev mesafesiyle hesaplanıyor (çapraz komşu = 1). ARCHITECTURE.md, çapraz hareketin hâlâ
> düz harekete göre üstün olduğunu bilinen bir açık olarak yazıyor. `CharacterStats.Jump`
> tanımlı ama bağlı değil; tüm birimler grid genelindeki `maxStepHeight` değerini kullanıyor.
>
> **Öneri:** (a) FFT gibi **Manhattan**'a geçmek çapraz kayma sorununu kökten çözer ve hareketle
> menzili aynı dile sokar. (b) `Jump`'ı `GetTilesInMoveRange(..., maxStepHeightOverride)`
> parametresine bağla; parametre zaten bunun için var. (c) Yüksekliği menzil/hasar bonusu olarak
> kullan.

---

## 6. Statlar ve hasar

### Ana statlar
HP, MP, **PA** (fiziksel güç), **MA** (büyü gücü), **Speed**, **Move**, **Jump**, **Brave**,
**Faith**.

### Silaha göre saldırı formülleri (BMG 2.1)

| Silah | Hasar |
|---|---|
| Kılıç, mızrak, rod, crossbow | `PA × WP` |
| Knight Sword, Katana | `[PA × Br / 100] × WP` |
| Hançer, Ninja kılıcı, Longbow | `[(PA + Speed) / 2] × WP` |
| Staff, Stick | `MA × WP` |
| Balta, flail, çanta | `(1..PA) × WP` (rastgele) |
| Fiziksel silah (gun) | `WP × WP` |
| Çıplak el | `[PA × Br / 100] × PA` |
| Çalgı, sözlük, kumaş | `[(PA + MA) / 2] × WP` |

- **Kritik vuruş:** Yaklaşık %5. Hasar normal ile neredeyse 2 katı arasında olur; %50 ihtimalle
  hedef bir kare geri itilir.
- **Martial Arts:** Silahsız saldırıda (elemental olmayan) stat ×3/2.
- **Defans statı yok.** Hasar azaltma kaçınma, Protect/Shell durumları ve zodiac ile yapılır.

**Neden işe yarıyor:** Her silah türü farklı bir statı ödüllendirir (Speed, Brave, MA). Bu yüzden
job ve ekipman seçimi stat profiline göre anlam kazanır.

> **Tactica'da şu an:** `Power + Strength − Defense`, en az 1. `Magic` kullanılmıyor.
> **Öneri:** `AbilityDefinition`'a bir "ölçekleme statı" alanı (Strength/Magic/Speed karışımı)
> ekle. FFT'nin çarpımsal formülleri (`PA × WP`) toplamsal formüllere göre hızlı büyür; ölçeği
> erkenden seç.

### Büyü formülü (BMG 3.2)
```
hasar = [CFa × TFa × Q × MA × (elemental) / 10000]
```
CFa = büyücünün Faith'i, TFa = hedefin Faith'i, Q = büyünün sabiti.

---

## 7. Brave ve Faith: iki yönlü statlar

| | Brave (0–100) | Faith (0–100) |
|---|---|---|
| Etkisi | Reaksiyon yeteneklerinin tetiklenme oranı **Br%**. Knight Sword/Katana/çıplak el hasarı. Move-Find Item'da yaygın eşya olasılığı Br%. | Büyü hasarı **ve** büyüden alınan hasar (`CFa × TFa`). Büyü isabet oranları. |
| Düşükse | Br < 10: **Chicken** durumu | Büyüden az etkilenir (iyileştirme dahil) |
| Uç değer cezası | Kalıcı Brave ≤ 5 ise birim savaş sonunda **kadrodan ayrılır** | Kalıcı Faith ≥ 94 ise birim **kadrodan ayrılır** |

- Savaş içi değişimlerin bir kısmı kalıcı olur (yaygın bilgi: değişimin 1/4'ü, **doğrulanmadı**).

**Neden işe yarıyor:** Faith **çift taraflı**. Yüksek Faith'li büyücü çok vurur ama düşman
büyüsüne de cam gibidir. Tek bir sayı, risk/ödül dengesi kurar. Uç değerlerdeki "kadrodan ayrılma"
cezası da sınırsız optimize etmeyi engeller.

> **Öneri (düşük-orta öncelik):** Reaksiyon yetenekleri gelince tetiklenme oranını Brave'e bağla.
> Faith'in çift yönlü çarpanı, Tactica'nın Magic statına ucuz ve derin bir boyut ekler.

---

## 8. Zodiac uyumu

Her birimin bir burcu var. Saldıran ile hedef arasındaki uyum, hasarı ve başarı oranını değiştirir
(BMG 1.3):

| Uyum | Çarpan |
|---|---|
| Best | +%50 |
| Good | +%25 |
| Nötr | 0 |
| Bad | −%25 |
| Worst | −%50 |

- Karşı burçlarda: aynı cinsiyet Worst, karşı cinsiyet Best. Canavarlara karşı Bad sayılır.
- Kaçınmayı etkilemez.

**Not:** Oyuncular çoğunlukla bu sistemi görmez; "neden bu kadar az vurdu?" diye sorulan gizli
bir değişkendir. **Tactica için önerilmez.** Okunabilirliği düşürür, karşılığında az derinlik
verir. Eklenecekse UI'da açıkça gösterilmeli.

---

## 9. Job sistemi

- **JP kazanımı** (BMG 6.3), her başarılı aksiyonda:
  ```
  JP = [(8 + JobLevel × 2 + [Lv / 4]) × M]        M = 3/2 (JP Boost ile)
  ```
- **JP paylaşımı:** Diğer **tüm kadro üyeleri** kazanılan JP'nin **1/4**'ünü kendi aktif
  job'larına alır. Yedekteki birimler de ilerler.
- **Job level eşikleri:** 0 (0–99 JP) → 8 (2100+ JP).
- **Yetenek slotları:**
  1. **Primary:** Mevcut job'un komut seti
  2. **Secondary:** Öğrenilmiş başka bir job'un komut seti
  3. **Reaction:** Saldırıya tepki (Counter vb.), Br% oranında tetiklenir
  4. **Support:** Pasif (Attack Up, JP Boost, Martial Arts…)
  5. **Movement:** Move +1, Jump +2, Teleport…
- Öğrenilen yetenek **kalıcı**: Move +2'yi bir kez öğrenen, onu her job'da takabilir.
- Job'lar ağaç şeklinde açılır; Squire/Chemist seviyeleri daha ileri job'ları açar.

**Neden işe yarıyor:**
1. **Yatay ilerleme.** Güç, seviyeden çok yetenek kombinasyonlarından gelir (örn. Ninja + Martial
   Arts + Attack Up).
2. **JP paylaşımı** kadronun tamamını bir arada tutar; yedekte kalan birim geride kalmaz.
3. **Her aksiyon JP verir.** Oyuncu pasif kalmak yerine bir şey yapmaya teşvik edilir.

> **Tactica'da şu an:** `JobDefinition` seviyeye göre stat büyümesi ve seviyeye bağlı yetenek açma
> içeriyor. Birim job değiştiremiyor; secondary/reaction/support/movement slotları yok. Seviye
> atlama görev başına +1 (geçici çözüm).
>
> **Öneri (yüksek öncelik, büyük iş):** `RosterUnit`'e job başına JP ve öğrenilmiş yetenekler
> ekle. İlk adım olarak Primary + Secondary + bir Support slotu yeterli. JP paylaşımını (1/4) baştan
> koy; roster sistemiyle doğal olarak uyuşuyor.

---

## 10. EXP ve seviye (BMG 6.3)

```
Exp = [max(10 + (hedefLv − kendiLv) + D, 1) × M]        M = 2 (Exp Boost ile)
D (öldürme bonusu): ilk +10, sonra 0, −4, −5, −6, −7, sonra hep −8
```

- Her aksiyon EXP verir; yüksek seviyeli hedef daha çok verir. 100 EXP = 1 seviye.
- Çoklu hedefli aksiyonda en yüksek tekil değer alınır.

**Tasarım dersi:** Seviye farkı terimi "zayıflarla grind"i yavaşlatır, zor hedefi ödüllendirir.

---

## 11. KO, ölüm sayacı ve kalıcı ölüm

- HP 0 olan birim **KO** olur ve başında **3**'ten başlayan bir sayaç belirir (BMG 5.4).
- Sayaç birimin her AT'sinde 1 azalır (CT dolmaya devam eder ama tur yerine sayaç düşer).
- Sayaç **−1**'e inerse birim **%50 kristal / %50 hazine sandığına** dönüşür ve **kalıcı olarak
  kaybedilir**.
  - **Kristal:** Toplayan birimin HP/MP'si dolar ya da ölen birimin yeteneğini öğrenebilir
    (aynı job ve insan olması şartıyla).
  - **Sandık:** Rastgele eşya, ekipmanı dahil.
- Sayaç bitmeden Phoenix Down veya Raise ile canlandırılabilir.
- Ramza'nın sayacı biterse **Game Over**.

**Neden işe yarıyor:** Ölüm ani değil; **3 turluk bir gerilim penceresi** var. Oyuncu "kurtarmak
için riske gireyim mi?" kararı verir. Kalıcı kayıp, roster'a duygusal ağırlık katar.

> **Tactica'da şu an:** KO olan birim karesini kapatıyor ama tur almıyor (bu refactorda değişti).
> Görev sonunda roster birimleri her durumda kadroda kalıyor.
>
> **Öneri (orta öncelik):** `GridUnit`'e `DeathCounter` ekle. KO olan birimin turu gelince sayaç
> düşsün; sayaç bitince `MissionUnitReport.PermanentlyLost` olsun ve `CampaignState` birimi
> kadrodan silsin. Turu atlama kodu (`EndCurrentTurn` döngüsü) sayaç düşürme için doğru yer.

---

## 12. Kadroya katma

| Yol | Nasıl |
|---|---|
| **Hikâye** | Senaryo gereği katılan karakterler |
| **Satın alma** | Şehirlerde jenerik birim tutma |
| **Invite** (Mediator/Orator) | Düşman insanı veya canavarı davet etme. Başarı oranı tabanı `MA + 20`. Davet edilen birim savaştan **sonra** katılır. |
| **Canavar Train/Poach** | Kritik durumdaki canavara davet; Poacher ile öldürülen canavar dükkâna eşya olarak düşer |
| **Kadrodan çıkma** | Brave ≤ 5 veya Faith ≥ 94 → birim ayrılır |

> **Tactica'da şu an:** Sonuç ekranında "kazandıysan herkes, kaybettiysen senin tarafında
> bitirenler" kuralı ve debug faction paneli var. Faction değişimi zaten kalıcı katılımın yolu.
>
> **Öneri:** Gerçek bir **Invite** yeteneği ekle (`AbilityEffectType.Recruit`). Başarılı olursa
> hedefe `SetFaction(Player)` uygula ve birim savaşın geri kalanında senin tarafında kalsın.
> Bu, mevcut "senin tarafında bitiren katılabilir" kuralıyla birebir örtüşüyor. Sonuç ekranındaki
> "kazandıysan herkesi al" kuralını o zaman kaldır.

---

## 13. Görevler ve zafer koşulları

- Tipik koşullar: **tüm düşmanları yen**, **lideri yen**, **bir misafiri (guest) koru**.
- Misafirler AI kontrolünde olabilir; koruma görevlerinin zorluğu buradan gelir.
- Savaş öncesi **formasyon**: hangi birimlerin hangi başlangıç karelerine yerleşeceği seçilir.

> **Tactica'da şu an:** Tek koşul "bir taraf düştü". PROGRESS.md "görev hedefleri bir üst katmanda
> değerlendirilmeli" diyor; doğru yaklaşım.
>
> **Öneri:** `MissionDefinition`'a `Objective` (DefeatAll / DefeatTarget / Protect) ekle ve
> `MissionRunner`'da değerlendir. `CheckCombatEndCondition`'a dokunma; belgelenmiş karar bu.

---

## 14. Arayüz ve okunabilirlik (FFT'nin zayıf ve güçlü yanları)

**Güçlü:**
- Hedeflemeden önce **isabet % ve tahmini hasar** gösterilir. Oyuncu hesap yapmadan karar verir.
- Charge'lı büyüler haritada işaretlenir; kimin ne zaman hareket edeceği görülebilir.
- 90° kamera dönüşü + yükseklik göstergesi ile 3D harita okunabilir kalır.

**Zayıf (kaçınılacaklar):**
- Zodiac ve bazı formüller oyuna gömülü ve görünmez.
- Brave/Faith'in kalıcı değişimi ve kadrodan ayrılma uyarısı belirsiz.
- Charge sürelerinin "tick" olarak ifadesi soyut; turn order listesi olmadan zor okunur.

> **Öneri (hızlı kazanım):** Hedef seçerken isabet ve tahmini hasar önizlemesi göster.
> `ActionResolver.CalculateDamage` zaten yan etkisiz; doğrudan HUD'a bağlanabilir.

---

## 15. Tactica için öncelikli yol haritası

> Bu tablo FFT araştırmasından çıkan ilk öneri listesiydi. Projenin güncel yol haritası artık
> [PROGRESS.md](PROGRESS.md)'de (Faz 8–15), tasarımın kendisi [GAME_DESIGN.md](GAME_DESIGN.md)'de.

> **Durum (2026-10-24):** 1, 2, 3, 4 ve 8 uygulandı, cyberpunk temasına uyarlanarak:
> CT saati + **Turn Queue**, tur başına tek aksiyon (**Act** menüsü), hasar/şans önizlemesi,
> **Flatline** sayacı (ölüm sayacı; süre biterse kalıcı kayıp) + **Reboot Protocol** (Raise),
> **System Override** (Invite; `Power + Magic`%, başarılı hack birimi senin tarafına geçirir ve
> görev sonunda ekibe katılabilir). Menüler: Safehouse / Contracts / Crew / Hire Runners / Jack Out.
> İkinci tur: 5 (**facing + evasion**: önden tam, yandan yarım, arkadan sıfır; neon vizör yönü
> gösteriyor), 6 (**Manhattan** hareket/menzil + birim başına **Jump**), 9 (**görev hedefleri**:
> Neutralize ve Protect; yeni kontratlar Cut the Signal, Courier Run). AI artık düşen müttefikini
> Reboot ediyor.
> Üçüncü tur: FFT'nin **guest** modeli (taraf ve kontrol ayrı; Civilian ve Courier AI kontrollü
> müttefik), Neutral yalnızca herkese düşman üçüncü taraflar için. Kontrat başına **üretilen
> haritalar**: teraslar, girintili kenarlar, delikler, Jump'a göre tırmanılabilen siperler.
> Sırada: 7 (JP ve yetenek slotları), elle yön seçimi, Brave/Faith.

| # | Özellik | Neden | Nereye dokunur |
|---|---|---|---|
| 1 | **Tur başına tek aksiyon** | Diğer her sistemin varsayımı | `PlayerActionController`, `GridUnit.HasAttackedThisTurn` |
| 2 | **CT tabanlı tur sistemi** + turn order şeridi | FFT'nin kimliği; Speed'e anlam verir | `CombatManager.BuildInitiativeOrder` / `EndCurrentTurn`, HUD |
| 3 | **Hasar/isabet önizlemesi** | Ucuz, büyük UX kazancı | `CombatHUD`, `ActionResolver` |
| 4 | **Ölüm sayacı + kalıcı kayıp** | Roster'a ağırlık katar | `GridUnit`, `CombatManager`, `CampaignState` |
| 5 | **Facing + evasion** | Konum oyununu derinleştirir | `GridUnit`, `ActionResolver`, Wait akışı |
| 6 | **Manhattan hareket/menzil** ve **Jump** | Bilinen açığı kapatır, FFT hissi | `GridManager.GetTilesInMoveRange`, `GetGridDistance` |
| 7 | **JP + Secondary/Support slotları** | Uzun vadeli build derinliği | `RosterUnit`, `JobDefinition`, roster ekranı |
| 8 | **Invite yeteneği** | Faction sisteminin doğal kullanımı | `ActionResolver`, `AbilityEffectType` |
| 9 | **Görev hedefleri** | Görev çeşitliliği | `MissionDefinition`, `MissionRunner` |
| 10 | Brave/Faith | Reaksiyon ve büyü sistemi gelince | `CharacterStats` |

**Alınmaması önerilenler:** Zodiac (görünmez karmaşıklık), uç Brave/Faith'te kadrodan ayrılma
(cezalandırıcı sürpriz), rastgele hasarlı silahlar (öngörülemez).

---

## Kaynaklar

- Aerostar, *FFT Battle Mechanics Guide* v6.5 — [m-l.org/~greerga/fftnet/fftmech](http://www.m-l.org/~greerga/fftnet/fftmech/) (birincil kaynak: CT, evasion, zodiac, formüller, JP/EXP, KO)
- [Tactics League — Knowledge](https://tacticsleague.com/learn/knowledge/) (CT sonrası değerler, Jump, kritik)
- [Tactics League — Weapons](https://tacticsleague.com/learn/weapons/)
- [Game8 — CT Explained](https://game8.co/games/Final-Fantasy-Tactics/archives/543470), [Game8 — When a Unit Dies](https://game8.co/games/Final-Fantasy-Tactics/archives/543565)
- [Final Fantasy Wiki — Charge time](https://finalfantasy.fandom.com/wiki/Charge_time), [KO (Tactics)](https://finalfantasy.fandom.com/wiki/KO_(Tactics)) (arama özetlerinden; sayfalar doğrudan okunamadı)
- [RPG Site — How Bravery and Faith works](https://www.rpgsite.net/guide/18606-final-fantasy-tactics-the-invalice-chronicles-how-bravery-faith-works)
- [Wikipedia — Yasumi Matsuno](https://en.wikipedia.org/wiki/Yasumi_Matsuno), [GamingTrend — Ivalice Chronicles review](https://www.gamingtrend.com/reviews/final-fantasy-tactics-the-ivalice-chronicles-review-blood-is-the-price-of-progress-it-is-the-ink-in-which-historys-pages-are-writ) (diorama fikri, Tactics Ogre bağlantısı)
- [Hack the Minotaur — FFT guides](https://hacktheminotaur.com/final-fantasy-tactics-the-ivalice-chronicles/beginner-tips/) (yetenek slotları, JP Boost)
