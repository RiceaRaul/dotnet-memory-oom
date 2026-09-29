# MemoryDiagnostics – copy-paste în orice aplicație ASP.NET Core (.NET 8+)

## Ce face
- `MemoryMonitor`: loghează periodic memoria folosită / limita containerului, heap-ul GC, RSS și thread-urile,
  și scrie **un dump înainte de OOMKilled** când memoria trece de `DumpThresholdPercent`.
- `FailFastOnOutOfMemory` (opțional): la `OutOfMemoryException` oprește procesul ca să se scrie crash dump-ul.
- **Nu face nimic dacă `MemoryDiagnostics__Enabled` nu e `1`** (sau `true`). Implicit e oprit.

## Integrare (3 pași)
1. Copiezi folderul `MemoryDiagnostics/` în proiect (namespace-ul e `MemoryDiagnostics`, independent de proiect).
2. Adaugi pachetul:
   ```
   dotnet add package Microsoft.Diagnostics.NETCore.Client
   ```
3. În `Program.cs`:
   ```csharp
   using MemoryDiagnostics;

   builder.Services.AddMemoryDiagnostics(builder.Configuration);
   ```

## Configurare
`appsettings.json` (toate cheile sunt opționale, valorile de mai jos sunt cele implicite):
```json
"MemoryDiagnostics": {
  "Enabled": "0",
  "IntervalSeconds": 15,
  "DumpThresholdPercent": 80,
  "DumpDirectory": "dumps",
  "DumpType": "WithHeap",
  "FallbackLimitMb": 512,
  "FailFastOnOutOfMemory": false
}
```
Aceleași chei ca env vars în pod (au prioritate față de appsettings):

| Env var | Exemplu | Rol |
|---|---|---|
| `MemoryDiagnostics__Enabled` | `1` | **flag-ul de start**; orice altă valoare = oprit |
| `MemoryDiagnostics__IntervalSeconds` | `15` | cât de des loghează |
| `MemoryDiagnostics__DumpThresholdPercent` | `80` | pragul pentru dump; `0` = doar loguri, fără dump |
| `MemoryDiagnostics__DumpDirectory` | `/dumps` | folder scriibil (volum montat în pod) |
| `MemoryDiagnostics__DumpType` | `WithHeap` | `WithHeap` (recomandat) / `Full` / `Normal` / `Triage` |
| `MemoryDiagnostics__FallbackLimitMb` | `512` | limita folosită local, când nu rulezi în container |
| `MemoryDiagnostics__FailFastOnOutOfMemory` | `true` | oprește procesul la OOM → crash dump |

Pentru `FailFastOnOutOfMemory` ai nevoie și de env vars-urile runtime-ului:
```
DOTNET_DbgEnableMiniDump=1
DOTNET_DbgMiniDumpType=2
DOTNET_DbgMiniDumpName=/dumps/oom_%p_%t.dmp
```

## Pod (OpenShift / Kubernetes)

### Minim obligatoriu
| Ce | De ce |
|---|---|
| `MemoryDiagnostics__Enabled=1` | fără el nu pornește nimic |
| volum `emptyDir` montat pe `/dumps` + `MemoryDiagnostics__DumpDirectory=/dumps` | cu UID aleator folderul aplicației nu se poate scrie; fără volum ai doar loguri, iar dump-ul eșuează (vezi warning-ul la start) |
| sink Serilog **Console** | altfel logurile de memorie nu apar în `oc logs --previous` |

Nu trebuie root și nici capabilități în plus: `/sys/fs/cgroup` e citibil de orice UID (cgroup v1 și v2 sunt ambele suportate).
`DOTNET_EnableDiagnostics` nu trebuie să fie `0`.

### Opțional: dump și la `OutOfMemoryException`
`MemoryDiagnostics__FailFastOnOutOfMemory=true` + `DOTNET_DbgEnableMiniDump`, `DOTNET_DbgMiniDumpType`, `DOTNET_DbgMiniDumpName`.

### Complet (toate env-urile, comentate: `pod-env.yaml`)
```yaml
env:
  - name: MemoryDiagnostics__Enabled
    value: "1"
  - name: MemoryDiagnostics__DumpDirectory
    value: /dumps
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
- Cu Serilog: adaugă un sink **Console**, altfel logurile nu apar în `oc logs <pod> --previous`.
- Dump-ul rămâne în `emptyDir` după restart-ul containerului: `oc cp <pod>:/dumps ./dumps`.
- Ca să oprești monitorul: `MemoryDiagnostics__Enabled=0` (sau ștergi variabila) și faci redeploy prin ArgoCD.

> ⚠️ Dump-urile conțin secrete și date din memorie. Nu le comite și șterge-le după analiză.
