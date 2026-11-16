# Architecture technique

Carte du code et des données pour reprendre le développement. Les offsets ci-dessous
valent pour les exécutables `mafia2.exe` **vérifiés** (voir §5) ; ne pas les transposer
à une autre version sans réanalyse.

## 1. Vue d'ensemble

- Trainer **C# / Windows Forms**, cible `net10.0-windows`, compilé en **x64**
  (`UseAppHost=false` → `EmpireBayTrainer.dll` lancé par le runtime .NET).
- Le jeu `mafia2.exe` est en **x86 / 32 bits**. Le trainer agit sur lui par **P/Invoke**
  (`ReadProcessMemory`, `WriteProcessMemory`, allocation distante, contrôle de threads).
- Deux mécanismes d'action :
  1. **Écritures mémoire** directes (santé, argent, carburant…), revalidées à chaque
     tick ;
  2. **Code-caves x86** générés en C# (hooks munitions / recul / précision) et
     **commandes Lua** exécutées dans le contexte du jeu (ajout d'arme, spawner…).
- Pas d'injection graphique, pas d'overlay.
- Boucle moteur ~100 ms ; rafraîchissement UI ~200 ms ; verrou partagé contre les
  opérations concurrentes.

## 2. Fichiers sources (`src/`)

| Fichier | Rôle |
| --- | --- |
| `Trainer.cs` | Interface, session/processus, snapshots joueur/inventaire/portefeuille, moteur de cheats, raccourcis, journal, tests généraux et points d'entrée CLI (`--self-test`, etc.). |
| `Hooks.cs` | Générateur x86 `X86` ; hooks munitions / recul / précision ; restauration ; exclusion de la MG42. |
| `Experimental.cs` | Catalogue d'armes, commandes Lua, dispatcher dans le callback joueur, file d'actions. |
| `VehicleScalars.cs` | Plein direct : repérage du réservoir, contrôle d'identité, une écriture sur la quantité. |
| `GamePatch.cs` | Patch 4 Go / LargeAddressAware : détection du drapeau PE, application sauvegardée, localisation de `mafia2.exe`, test pur. |
| `BodyRepair.cs` | Réparation carrosserie différée par le planificateur du jeu, avec contrôle de fin. |
| `PoliceProtection.cs` | Prévention policière ciblée (prévention, pas effacement). |
| `VehicleSpawner.cs` | Catalogue des modèles chargés + création native limitée. |
| `ForceLoad.cs` | Chargement forcé — **inutilisé** (piste abandonnée, conservée pour référence). |
| `*Tests.cs` | Tests ciblés hors jeu, lancés par `--self-test`. |

## 3. Cheats : principes de sûreté

- **Écritures per-tick** : le worker ré-`Inspect()` et **revalide tous les pointeurs à
  chaque tick** (lève une exception en cas d'incohérence) → on n'écrit que sur des
  pointeurs frais et validés.
- **Hooks per-tir** : gardés — propriétaire = joueur local (`[ecx+0x174] == joueur`),
  ligne d'arme dans les bornes de la table (`[ecx+0x1a8]`), et **null + plage** sur les
  pointeurs déréférencés. La MG42 (ID 14) est exclue du sans-recul.
- **Suspension de transition** : tant que le joueur n'est pas en contrôle
  (`joueur+0x22` bit `0x20`), + marge, le trainer retire les hooks et n'écrit rien.

## 4. Offsets connus (exe vérifié)

> À vérifier au désassemblage avant toute modification.

- Image habituelle du jeu : `0x400000`.
- Singleton joueur : `image + 0x16BFE14` ; joueur : `singleton + 0x118`.
- Inventaire : `joueur + 0xA0` ; script : `+ 0xA4` ; voiture : `+ 0x64` ;
  contrôleur d'arme : `+ 0xB0` ; santé : `+ 0xD0` ; max : `+ 0xD4` ; drapeau contrôle :
  `+ 0x22` (bit `0x20`).
- Contrôleur d'arme : propriétaire `+ 0x174`, ligne de données `+ 0x1A8`.
- Hooks : commandes `image + 0x3BD42` (appel d'origine `+ 0x2ABE0`) ; recul
  `+ 0x20E60` ; précision `+ 0x208B0` ; munitions `+ 0x5A46E0`.
- Dispatcher Lua : chargement Lua `image + 0x1D5BD0` (**cinq** arguments), pcall
  `image + 0x1D3F80` ; préservation registres/flags/x87/SSE (FXSAVE/FXRSTOR), pile Lua
  restaurée en tenant compte d'une réallocation.
- Plein : voiture `+ 0x2EC` → conteneur ; tableau `+8/+12` ; quantité `+ 0x1CC`,
  capacité `+ 0x1D0`.
- `Values.Ptr` accepte les adresses 32 bits au-delà de `0x7FFFFFFF` jusqu'à
  `0xFFFEFFFF` (nécessaire avec le jeu patché 4 Go). `NativeMemory.Allocate` garde une
  limite différente à `0x7FFFFFFF` — ne pas confondre.

## 5. Le patch 4 Go (`GamePatch.cs`)

- Drapeau `IMAGE_FILE_LARGE_ADDRESS_AWARE` (`0x0020`) dans l'en-tête PE, à l'offset
  `e_lfanew + 22` (Characteristics du COFF), après contrôle `MZ` / `PE\0\0` /
  machine x86 `0x14c`. **Détection par le drapeau** → indépendante de la version.
- `Apply()` refuse si le jeu tourne, sauvegarde l'exe original
  (`app\PatchBackup\mafia2.exe.original-<date>`), écrit 2 octets, puis **revérifie**.
- Hash SHA256 de `mafia2.exe` connus et acceptés par le trainer :
  - original : `E389B275C40C3623AC0F646E9F42FF875011254F2A92100A18DB545FB66E112F`
  - patché 4 Go : `88F60CD24C8D2FB874526B0559E55EF447C3B3A288DBFC4D2B9A4BB0770023C6`

## 6. Pièges connus

- Les pages de code allouées ne sont pas libérées immédiatement au retrait d'un hook
  (un thread peut encore y revenir) ; les routines d'origine sont restaurées. Ne pas
  transformer cela en `VirtualFreeEx` systématique sans analyse de sûreté.
- `ExecutionState` et `LastCompletionState` n'ont pas le même cycle de vie : une action
  ponctuelle retire son callback, mais le résultat doit rester consultable après. La
  **soumission** d'une réparation et sa **fin différée** sont deux phases distinctes.
- Les actions attendent au plus **10 s** le prochain tick ; la carrosserie ajoute un
  délai de **500 ms** via le planificateur du jeu, puis un contrôle de fin.
