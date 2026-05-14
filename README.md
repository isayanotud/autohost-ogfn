# AutoHost2ftn


# FRANCAIS 


- démarre/valide `lawinserver.exe`
- met à jour `Config/config.ini` pour le matchmaker local
- lance `FortniteLauncher.exe` et `FortniteClient-Win64-Shipping_EAC.exe`, puis les suspend
- lance `FortniteClient-Win64-Shipping.exe` avec les args Reboot host/headless
- injecte `cobalt.dll`, puis le DLL gameserver après login
- ping le gameserver UDP local et auto-restart à la fin de match ou après crash

## Lancer

```powershell
.\bin\Release\net8.0\win-x64\publish\AutoHost.exe
```

À lancer en administrateur si l'injection DLL échoue.

## Config

Le fichier `autohost.json` est créé automatiquement. Les chemins déjà mis :

- `FortniteRoot`: `C:\Users\ban2f\Downloads\24.20 FreeBuild\24.20 FreeBuild`
- `LauncherRoot`: `C:\Users\ban2f\Desktop\Reboot-Launcher-10.0.9\Reboot-Launcher-10.0.9`

Commandes utiles :

```powershell
.\bin\Release\net8.0\win-x64\publish\AutoHost.exe --init
.\bin\Release\net8.0\win-x64\publish\AutoHost.exe --check
.\bin\Release\net8.0\win-x64\publish\AutoHost.exe --once
```

`--check` valide les chemins et extrait automatiquement le fallback Reboot DLL depuis le launcher si `GameServerDll` n'est pas renseigné.


# ENGLISH 

starts/validates lawinserver.exe
updates Config/config.ini for the local matchmaker
launches FortniteLauncher.exe and FortniteClient-Win64-Shipping_EAC.exe, then suspends them
launches FortniteClient-Win64-Shipping.exe with Reboot host/headless args
injects cobalt.dll, then injects the gameserver DLL after login
pings the local UDP gameserver and auto-restarts after a match ends or after a crash
Launch
.\bin\Release\net8.0\win-x64\publish\AutoHost.exe

Run as administrator if DLL injection fails.

Config

The autohost.json file is created automatically. Default paths already set:

FortniteRoot: C:\Users\ban2f\Downloads\24.20 FreeBuild\24.20 FreeBuild
LauncherRoot: C:\Users\ban2f\Desktop\Reboot-Launcher-10.0.9\Reboot-Launcher-10.0.9

Useful commands:

.\bin\Release\net8.0\win-x64\publish\AutoHost.exe --init
.\bin\Release\net8.0\win-x64\publish\AutoHost.exe --check
.\bin\Release\net8.0\win-x64\publish\AutoHost.exe --once

--check validates paths and automatically extracts the fallback Reboot DLL from the launcher if GameServerDll is not configured.
