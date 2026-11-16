# Empire Bay Trainer

Trainer **solo** pour **Mafia II (Classic)** sur PC. Application Windows séparée qui
s'ouvre à côté du jeu, avec des interrupteurs, des raccourcis clavier et des actions
ponctuelles. Elle s'attache au processus `mafia2.exe` et agit par lecture/écriture
mémoire ciblée et par quelques commandes exécutées dans le contexte du jeu — **aucune
injection graphique, aucun overlay**.

> Réservé au **solo de Mafia II Classic** (32 bits). Pas pour Mafia II: Definitive
> Edition ni pour le multijoueur. Aucun cheat ne s'active automatiquement : il faut se
> connecter à une partie chargée, puis activer chaque option à la main.

---

## Fonctions

| Fonction | Résumé |
| --- | --- |
| Invincibilité | Drapeau natif + santé remontée au maximum si elle est en dessous. |
| Munitions infinies / chargeur illimité | Maintenus par un hook interne du jeu (pas d'écriture externe du chargeur). |
| Argent | +10 000 $ ponctuel, ou mode illimité (plancher ~999 999 $). |
| Sans recul + précision | Universel sur toutes les armes de la table **sauf la MG42 fixe** (retirée automatiquement quand la MG42 est équipée). |
| Plein | Une seule écriture sur la quantité du réservoir vérifié. À bord d'un véhicule. |
| Réparer le moteur | Dégâts moteur remis à zéro. À l'arrêt, hors cinématique. |
| Réparer la carrosserie | Réparation différée par le planificateur du jeu ; à l'arrêt, seul au volant, hors séquence scénarisée. |
| Jamais recherché | **Prévention** de la recherche policière (à activer *avant* les délits). N'efface pas une recherche déjà active. |
| Ajout d'armes | Donne une arme du catalogue chargé (via l'API native du jeu). ⚠ Voir l'avertissement ci-dessous. |
| Spawner de véhicules | Fait apparaître une voiture **déjà chargée dans le chapitre**. Free-roam uniquement. |
| Patch 4 Go du jeu | Vérifie / installe le drapeau PE *LargeAddressAware* sur `mafia2.exe` (voir plus bas). |
| Garder le trainer visible | Maintient la fenêtre au-dessus du bureau (ce n'est pas un overlay plein écran). |

Raccourcis : `Ctrl+Alt+F1…F5` pour les interrupteurs principaux, `Ctrl+Alt+F6`
ajoute de l'argent, `Ctrl+Alt+F12` coupe les options.

### Avertissements importants

- **Armes hors chapitre / DLC.** Donner une arme qui n'appartient pas nativement au
  chapitre ou au DLC en cours peut l'afficher sans modèle en main, la rendre inerte,
  ou **faire planter le jeu dès qu'une fusillade / recherche policière démarre**
  (les ressources de l'arme ne sont pas chargées en mémoire — c'est une limite du jeu,
  pas une écriture du trainer). Préfère les armes déjà présentes dans ton chapitre.
- **Voiture spawnée = free-roam uniquement.** L'amener en customisation (Bomba), au
  garage ou dans une cinématique scénarisée peut provoquer un crash
  « pure virtual function call » (R6025) : l'entité n'est pas un véhicule pleinement
  enregistré par le jeu.
- Le trainer n'est **pas signé**. Le Contrôle intelligent des applications Windows
  (SmartScreen) peut le bloquer. Rien n'est modifié dans les protections Windows par
  l'application.

---

## Le patch « 4 Go » (LargeAddressAware)

`mafia2.exe` est un exécutable **32 bits**, donc limité à 2 Go d'espace d'adressage
par défaut. Sur un PC moderne, cette limite est atteinte et le jeu plante. Le patch
« 4 Go » active un unique drapeau dans l'en-tête PE
(`IMAGE_FILE_LARGE_ADDRESS_AWARE`), ce qui autorise le jeu à utiliser jusqu'à ~4 Go.
**Ce n'est pas un crack** : c'est une modification de deux octets dans l'en-tête, et
l'exe original est sauvegardé avant toute écriture.

Le bouton « Patch 4 Go du jeu » :
1. localise `mafia2.exe` (jeu lancé, puis Steam, puis sélecteur de fichier) ;
2. détecte le drapeau — s'il est déjà posé, il le signale ;
3. sinon, **jeu fermé**, demande confirmation puis l'installe (sauvegarde de l'exe
   dans `app\PatchBackup`).

La détection se fait par le drapeau lui-même, donc indépendamment de la version.

---

## Installation (utilisateur)

1. Installe le **runtime .NET 10 Desktop** (x64) si tu ne l'as pas :
   <https://dotnet.microsoft.com/download/dotnet/10.0> (« .NET Desktop Runtime »).
2. Décompresse l'archive de la *release* où tu veux.
3. Lance **`Lancer.vbs`** (ou crée un raccourci vers `wscript.exe` avec
   `//B //Nologo "chemin\Lancer.vbs"` pour éviter toute fenêtre de terminal).
4. Charge une partie dans Mafia II, puis clique **Connecter** dans le trainer.

> Le trainer est *framework-dependent* : il a besoin du runtime .NET installé, il ne
> l'embarque pas.

---

## Compilation (développeur)

Prérequis : **SDK .NET 10** sous Windows.

```powershell
cd src
.\Build.ps1
# ou :
dotnet build .\MafiaII-SoloTrainer.csproj -c Release -o .\app
```

Tests hors jeu (doit sortir avec le code 0) :

```powershell
dotnet .\app\EmpireBayTrainer.dll --self-test
```

Le projet est volontairement en **x64** (le trainer), alors que le jeu est en **x86** ;
l'accès mémoire inter-processus est géré par P/Invoke. Détails dans
[`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md).

---

## Contribuer / reprendre le projet

Le design et les correctifs continuent d'évoluer. Si tu reprends le projet (humain ou
assistant IA), lis d'abord :

- [`CONTRIBUTING.md`](CONTRIBUTING.md) — comment travailler sur ce projet, conventions
  de suivi, flux build/test/install ;
- [`docs/METHODE-DIAGNOSTIC.md`](docs/METHODE-DIAGNOSTIC.md) — **comment les bugs et
  crashes sont diagnostiqués** (par l'analyse, pas par tâtonnement) ;
- [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md) — carte technique (fichiers, offsets,
  hooks).

Une meilleure méthode de correction ou un meilleur design sont les bienvenus : ces
documents décrivent l'état et le raisonnement actuels, pas une contrainte figée.

---

## Avertissement

Projet personnel, fourni **tel quel**, sans garantie. Un trainer agit sur la mémoire
d'un jeu : malgré les gardes-fous (voir la méthode de diagnostic), un crash reste
possible dans une situation non testée. Utilise-le sur une partie solo et garde tes
sauvegardes. Voir [`LICENSE`](LICENSE).
