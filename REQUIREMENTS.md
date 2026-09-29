# Cerințe – Debug OOM .NET 10, varianta simplă

## Scop
Găsesc **ce obiect ține memoria** și îl repar, rulând local aplicația cu config-ul UAT,
cu debugger (Rider / VS) și snapshot-uri de memorie. Pod-ul are `limits.memory: 512Mi`.

## Ideea
OOMKilled în pod = procesul a trecut de 512Mi. În ~90% din cazuri vinovatul e heap-ul gestionat.
Local simulez limita pod-ului doar pentru GC:
- `DOTNET_GCHeapHardLimit` = ~384MB (75% din 512MB, cât alocă runtime-ul în pod)
- la depășire, runtime-ul aruncă `OutOfMemoryException`, în loc ca procesul să fie ucis
- debugger-ul se oprește pe excepție și un dump se scrie automat
- dump-ul arată ce tip ocupă memoria și cine îl ține în viață (GC root)

Fără Docker, fără watchdog, fără tool-uri în cluster.

## Cerințe funcționale
- **FR-1 Web API net10.0** cu câteva endpoint-uri care umplu memoria controlat (`mb=`):
  1. leak gestionat (listă statică)
  2. cache nelimitat (`IMemoryCache` fără `SizeLimit`)
  3. LOH (buffere mari `byte[]`)
  4. native (`NativeMemory.Alloc`), ca să vezi cum arată un leak pe care GC-ul NU îl vede
  5. `/memory`: heap GC, working set, limita efectivă
  6. `/reset`: eliberează tot
- **FR-2 Config**: `appsettings.json` + `appsettings.Uat.json`, `ASPNETCORE_ENVIRONMENT=Uat`, doar placeholdere, fără secrete.
- **FR-3 Un singur profil de rulare** în `launchSettings.json`, pornit cu F5 atât din Rider, cât și din VS:
  - `DOTNET_GCHeapHardLimit=0x18000000` (384MB)
  - `DOTNET_DbgEnableMiniDump=1`, `DOTNET_DbgMiniDumpType=4`, `DOTNET_DbgMiniDumpName=./dumps/oom_%p.dmp`
- **FR-4 Snapshot-uri: zero `dotnet tool`, doar pachete NuGet** (politica firmei nu permite tool-uri):
  - auto-dump la OOM: `createdump`, inclus în runtime, pornit prin env vars (FR-3)
  - `POST /diag/dump`: dump la cerere din aplicație (`Microsoft.Diagnostics.NETCore.Client` → `DiagnosticsClient.WriteDump`)
  - snapshot din IDE, în timpul debug-ului: VS Diagnostic Tools → *Take Snapshot*; Rider → debugger *Memory View* (gratuit)
- **FR-4b Analizor propriu de dump** (proiect consolă `DumpAnalyzer`, NuGet `Microsoft.Diagnostics.Runtime` / ClrMD):
  - `DumpAnalyzer <fisier.dmp>` → primele N tipuri după dimensiunea totală (număr de instanțe, bytes), echivalent `dumpheap -stat`
  - `--type <Nume>` → calea de la GC root la instanțe (static / thread / handle), echivalent `gcroot`
  - `--diff <a.dmp> <b.dmp>` → tipurile care au crescut între două dump-uri
  - motiv: analiza merge în Rider fără dotUltimate și fără `dotnet-dump`
- **Securitate**: dump-urile conțin config (connection strings, secrete UAT) și date din memorie → `./dumps` e în `.gitignore`; dump-urile nu se trimit în afara firmei
- **FR-5 README scurt, cu fluxul de 5 pași:**
  1. pornești cu F5 (profilul UAT cu limită)
  2. snapshot de baseline (IDE sau `/diag/dump`)
  3. reproduci traficul sau scenariul
  4. al doilea snapshot sau aștepți OOM → dump automat
  5. analizezi: `DumpAnalyzer` (oriunde) / VS (deschizi `.dmp` → *Debug Managed Memory*) / Rider (Memory View)

## Criterii de acceptare
- [ ] `POST /leak/managed?mb=500` → debugger-ul se oprește pe `OutOfMemoryException`, apare `./dumps/oom_*.dmp`
- [ ] `DumpAnalyzer` pe dump-ul de OOM: primul tip după dimensiune este cel alocat de scenariu, iar `gcroot` arată câmpul static
- [ ] `/leak/native` crește working set-ul, dar NU heap-ul GC → README explică: dacă local nu reproduci, dar pod-ul moare, suspectul e memoria nativă (abia atunci trecem la Docker `--memory=512m`)

## În afara scopului (acum)
Docker, kind/minikube, dotnet-monitor, dump pe prag, acces la cluster.

## Întrebări deschise
1. Aplicația reală rulează cu Server GC? (Server GC folosește mai multă memorie; profilul local trebuie să folosească aceeași setare)

## Pasul următor
`/sc:implement` (designul e suficient de simplu cât să nu mai fie nevoie de `/sc:design`)
