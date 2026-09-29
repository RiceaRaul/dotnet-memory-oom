# MemoryLab – dump automat la OOM (.NET 10)

Reproduce local limita de memorie a pod-ului (512Mi) și scrie automat un dump când aplicația rămâne fără memorie.

## Cum e configurat

### 1. Limita de memorie – `Properties/launchSettings.json`, profilul `Uat-OOM-512`
| Env var | Valoare | Rol |
|---|---|---|
| `DOTNET_GCHeapHardLimit` | `0x18000000` (384MB) | = 75% din 512Mi, exact cât își alocă GC-ul singur în pod |
| `DOTNET_gcServer` | `1` | aceeași setare ca în prod (ASP.NET Core folosește implicit Server GC) |
| `ASPNETCORE_ENVIRONMENT` | `Uat` | încarcă `appsettings.Uat.json` |

### 2. Dump-ul automat (`createdump`, inclus în runtime, nu trebuie instalat nimic)
| Env var | Valoare | Rol |
|---|---|---|
| `DOTNET_DbgEnableMiniDump` | `1` | activează scrierea dump-ului la crash |
| `DOTNET_DbgMiniDumpType` | `4` | dump complet (necesar pentru analiza heap-ului) |
| `DOTNET_DbgMiniDumpName` | `dumps/oom_%p_%t.dmp` | `%p` = PID, `%t` = timestamp; folderul e creat din `Program.cs` |
| `DOTNET_EnableCrashReport` | `1` | scrie și un `.crashreport.json` cu stack-ul fiecărui thread |
| `DOTNET_CreateDumpDiagnostics` | `1` | log detaliat al createdump (opțional, util când dump-ul nu apare) |

În pod, setările de mai sus se pun în `env:` din deployment, iar `dumps/` trebuie să fie un volum montat.

### 3. De ce e nevoie de `FailFast` (în `Program.cs`)
În pod, OOMKilled omoară procesul. Local, `OutOfMemoryException` e prinsă de Kestrel și devine un răspuns HTTP 500:
procesul rămâne viu și **nu se scrie niciun dump**. Hook-ul `FirstChanceException` → `Environment.FailFast` închide
procesul și declanșează `createdump`. Se activează cu `MemoryDiagnostics__Enabled=1` + `MemoryDiagnostics:FailFastOnOutOfMemory` (vezi `src/MemoryLab.Api/MemoryDiagnostics/README.md`).

## Test
1. Rider / VS: alegi profilul **Uat-OOM-512** → **Run** (nu Debug, vezi mai jos).
   CLI: `dotnet run --project src/MemoryLab.Api --launch-profile Uat-OOM-512`
2. Deschizi `src/MemoryLab.Api/MemoryLab.Api.http`:
   - `GET /memory` → `heapLimitMb: 384` confirmă că limita e aplicată
   - `POST /leak/managed?mb=500` → procesul moare
3. Rezultat: în `src/MemoryLab.Api/dumps/` apar `oom_<pid>_<ts>.dmp` (~700MB) și `.crashreport.json`.
   În log vezi `Process terminated. OutOfMemoryException - GCHeapHardLimit reached`.

## Run vs Debug
- **Run** (fără debugger): procesul moare și dump-ul se scrie automat. Folosești modul ăsta pentru analiza post-mortem.
- **Debug**: IDE-ul se oprește pe `FailFast` / `OutOfMemoryException` înainte de dump și ai stack-ul live.
  Pe Linux, `createdump` poate eșua cu debugger-ul atașat (un singur ptrace). În mod debug folosești snapshot-ul
  din IDE (Rider *Memory View*, VS *Diagnostic Tools → Take Snapshot*).

## Analiza dump-ului
- **VS (Windows):** *File → Open → File* pe `.dmp` → *Debug Managed Memory* → tipurile după dimensiune + *Paths to Root*.
  Un dump făcut pe Linux se deschide și în VS.
- **Rider:** dotMemory → *Import dump* (necesită dotUltimate).
- **`.crashreport.json`:** stack-ul thread-ului care a făcut alocarea (`managed_exception_type: System.OutOfMemoryException`).

> ⚠️ Dump-urile conțin config-ul UAT (connection strings, secrete) și datele din memorie. `dumps/` e în `.gitignore`.
> Nu trimite dump-urile în afara firmei.

---

# Direct în pod (OpenShift / Kubernetes)

## De ce nu vezi nimic în loguri
OOMKilled = kernel-ul trimite `SIGKILL` când containerul depășește limita. Procesul nu mai rulează nimic:
nu mai scrie în Serilog, nu mai rulează `createdump`. OpenShift repornește **containerul** în același pod
(crește `restartCount`), deci pod-ul pare același.

## 1. Confirmi motivul (fără cod)
```
oc describe pod <pod>          # Last State: Terminated, Reason: OOMKilled, Exit Code: 137
oc logs <pod> --previous       # logurile containerului omorât (doar ce a ajuns în stdout/stderr)
```
În consola OpenShift: Pod → *Logs* → dropdown-ul cu containerul → *previous*.
În ArgoCD: pod → *Live Manifest* → `status.containerStatuses[].lastState.terminated.reason`.

## 2. `MemoryMonitor` (folderul `MemoryDiagnostics/`, copy-paste: vezi README-ul din folder)
- La fiecare `IntervalSeconds` loghează: `used / limit` (working set din cgroup, aceeași valoare pe care o vede
  kubelet-ul), RSS, heap GC, thread-uri, numărul de GC-uri.
- Când `used >= DumpThresholdPercent`, scrie **un** dump în `DumpDirectory`, **înainte** ca procesul să fie omorât.
- Cum citești logul:
  - `GC heap` crește odată cu `used` → leak .NET → analizezi dump-ul (tipuri + GC roots)
  - `used` crește, `GC heap` rămâne mic → memorie nativă / thread-uri (exemplu: testul `/leak/native` mai jos)
- Serilog: adaugă un sink de **Console** pe lângă DB. Doar stdout ajunge în `oc logs --previous`; sink-ul de DB
  ține evenimentele în buffer și le pierde la kill.

## 3. Ce adaugi în deployment (prin git / Helm values, altfel ArgoCD anulează modificarea)
```yaml
env:
  - name: MemoryDiagnostics__Enabled
    value: "true"
  - name: MemoryDiagnostics__IntervalSeconds
    value: "15"
  - name: MemoryDiagnostics__DumpThresholdPercent
    value: "80"
  - name: MemoryDiagnostics__DumpDirectory
    value: /dumps
  # dump și la OutOfMemoryException (leak .NET care atinge limita GC înainte de cea a containerului)
  - name: MemoryDiagnostics__FailFastOnOutOfMemory
    value: "true"
  - name: DOTNET_DbgEnableMiniDump
    value: "1"
  - name: DOTNET_DbgMiniDumpType
    value: "2"
  - name: DOTNET_DbgMiniDumpName
    value: /dumps/oom_%p_%t.dmp
volumeMounts:
  - name: dumps
    mountPath: /dumps
volumes:
  - name: dumps
    emptyDir:
      sizeLimit: 2Gi
```
- `emptyDir` **rămâne** la restart-ul containerului (OOMKilled) și se șterge doar când pod-ul e șters.
  Copiezi dump-ul imediat: `oc cp <pod>:/dumps ./dumps`.
- Nu te baza pe folderul aplicației: în OpenShift rulezi cu UID aleator și `/app` nu e scriibil
  (testat: fără volum, aplicația crapă cu `UnauthorizedAccessException`).
- `DOTNET_EnableDiagnostics` **nu** trebuie să fie `0` în imagine/deployment, altfel dump-ul nu merge.
- Dump-ul are aproximativ cât memoria procesului (~0.5–0.7GB) → `sizeLimit` + ephemeral-storage pe nod.

## 4. Test local cu OOMKilled real (simulare OpenShift)
```bash
docker build -t memorylab:dev .
mkdir -p dumps-docker && chmod 777 dumps-docker
docker run -d --name memlab --memory=512m --memory-swap=512m -p 18080:8080 \
  -v "$PWD/dumps-docker:/dumps" --user 1000680000:0 --cap-drop ALL --security-opt no-new-privileges \
  -e MemoryDiagnostics__Enabled=1 -e MemoryDiagnostics__IntervalSeconds=2 memorylab:dev

curl -X POST 'localhost:18080/leak/native?mb=300'
curl -X POST 'localhost:18080/leak/native?mb=150'   # > 80% → dump
curl -X POST 'localhost:18080/leak/native?mb=200'   # > 512MB → OOMKilled
docker inspect memlab --format '{{.State.OOMKilled}} {{.State.ExitCode}}'   # true 137
docker logs memlab                                                             # = oc logs --previous
```
Rezultat verificat:
```
Memory 94% | used 482 MB / 512 MB | RSS 543 MB | GC heap 0 MB | ...
Dump written in 3101 ms: /dumps/memory_94pct_1_20260929_191215.dmp (649 MB)
OOMKilled=true ExitCode=137
```
`GC heap 0 MB` cu `used` aproape de limită = memorie nativă, nu leak .NET.

> ⚠️ Un dump din UAT conține connection strings, secrete și date din memorie. Cere acordul înainte să-l scoți din
> cluster, nu-l comite, șterge-l după analiză.
