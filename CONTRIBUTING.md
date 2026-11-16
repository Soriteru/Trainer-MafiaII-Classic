# Reprendre le projet (suivi et continuité)

Ce document sert à **retrouver le fil du projet** après une pause, que tu sois un
humain ou un assistant IA (Claude, ChatGPT/Codex…). Il explique comment on travaille
ici, où est la mémoire du projet, et comment enchaîner une nouvelle intervention sans
casser l'existant.

> Deux documents l'accompagnent :
> - [`docs/METHODE-DIAGNOSTIC.md`](docs/METHODE-DIAGNOSTIC.md) — **la démarche pour
>   trouver et corriger un bug / crash** (le cœur de la méthode) ;
> - [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md) — la carte technique (fichiers,
>   offsets, hooks).

---

## 1. Principe directeur

Le but est un trainer **solo stable** pour Mafia II Classic. La stabilité prime sur
la quantité de fonctions. Chaque fonction qui touche la mémoire du jeu doit être
**gardée** (vérifications de propriétaire, de bornes, de pointeur non nul) et
réversible.

**La méthode de correction et le design ne sont pas figés.** Si tu vois une meilleure
façon de corriger un bug, de structurer le code ou de présenter l'interface, fais-le —
mais documente le pourquoi (voir §4) et garde les gardes-fous.

---

## 2. La mémoire du projet

### Le journal de suivi (source de vérité détaillée)

L'historique complet — chaque crash, chaque hypothèse réfutée, chaque correctif, les
offsets et les hash de compilation — est tenu dans un **journal de suivi** daté. C'est
la mémoire longue du projet : **on le lit avant d'intervenir et on le met à jour
après.**

- Pour les mainteneurs d'origine, il est tenu localement (hors dépôt, car il contient
  des chemins de machine personnels) à l'emplacement convenu entre les collaborateurs.
- Si tu reprends le projet sans y avoir accès, ce dépôt (README + `docs/` + l'historique
  Git) contient l'état courant et le raisonnement ; repars de là et recommence un
  journal.

> **Pourquoi un journal séparé et pas seulement Git ?** Parce qu'on y consigne aussi
> ce qui **n'a pas** marché (pistes abandonnées, hypothèses fausses, crashes non
> reproductibles) et les validations **en jeu** faites manuellement par le joueur —
> des informations qui n'apparaissent pas dans un diff.

### Distinguer les niveaux de validation

Ne jamais présenter un essai hors jeu comme une validation en mission. On distingue :

1. **Préparé** — écrit / modifié.
2. **Compilé** — build OK, 0 erreur.
3. **Testé hors jeu** — `--self-test` sort avec le code 0.
4. **Testé en jeu** — le joueur confirme en situation réelle.
5. **Installé** — copié dans l'installation utilisée par le joueur, hash recalculé.

Un appel natif qui « réussit » (valeur de retour) n'est **pas** une preuve que l'effet
a eu lieu en jeu. On ne dit « confirmé » qu'après le niveau 4.

---

## 3. Flux de travail

### Compiler et tester

```powershell
cd src
.\Build.ps1
dotnet .\app\EmpireBayTrainer.dll --self-test   # doit sortir avec le code 0
```

Options de diagnostic utiles (voir `Trainer.cs` pour la liste à jour) :
`--self-test`, `--preview <png> <tabIndex>`, `--export-hooks`, `--export-experimental`,
`--live-check`, `--inspect`. **`--live-check` / `--benchmark` ne sont pas des tests
hors jeu** : ils touchent au processus du jeu.

### Ne jamais écraser une DLL en cours d'utilisation

Le trainer est un processus `dotnet` ouvert. Avant de remplacer `EmpireBayTrainer.dll`
dans une installation, **ferme le trainer** (proprement : laisse le jeu reprendre ses
ticks si « Jamais recherché » ou une action différée est en cours). Recalcule le
SHA256 après chaque compilation et note-le.

### Sauvegarder avant d'installer

Avant de remplacer les fichiers installés, copie l'ancienne version (binaires +
sources) dans un dossier de sauvegarde daté. On garde ainsi une possibilité de
rollback pour chaque étape.

### Le jeu doit tourner pour une validation en jeu

Pour compiler / self-test, le jeu peut être fermé. Pour toute validation en jeu, il
doit être **ouvert, au premier plan, hors pause** : le jeu suspend ses ticks en
arrière-plan ou en pause, et une commande exécutée dans son contexte ne partira pas.

---

## 4. Consigner une intervention

Après chaque changement notable, ajoute une entrée datée (dans le journal, et via un
commit Git clair) avec :

- **date réelle** et auteur (humain ou assistant) ;
- **le symptôme** traité et, pour un crash, son contexte précis (chapitre, cheats
  actifs, action au moment du plantage) ;
- **le diagnostic** : la cause trouvée *et* les hypothèses écartées ;
- **les fichiers** modifiés et le correctif concret ;
- **le niveau de validation** atteint (voir §2) et le hash de compilation ;
- **ce qui reste** à confirmer ou à faire.

Ne pas effacer l'historique : on actualise l'état courant *à côté* de l'historique.

---

## 5. Collaboration multi-assistants

Le projet a été mené avec plusieurs assistants (ChatGPT/Codex, Claude) et les essais
en jeu du joueur. Conventions :

- **Ne pas revendiquer le travail d'un autre.** Signe tes entrées.
- **Une seule source de vérité** : ne crée pas une copie divergente du journal.
- Respecte les décisions déjà prises et documentées (voir §6) tant qu'une nouvelle
  demande explicite ne les rouvre pas.

---

## 6. Décisions et limites déjà arrêtées

À ne pas rouvrir sans raison / demande explicite (détails et causes dans
`docs/ARCHITECTURE.md` et le journal) :

- **Effacer une recherche policière déjà active** : abandonné (fragile, sans effet
  propre sur les étoiles). On reste en **prévention**.
- **Spawn / chargement forcé d'un véhicule non chargé** : abandonné (la routine
  `SdsLoad` ne monte pas un modèle absent à la volée). Le spawner se limite aux
  modèles déjà chargés.
- **Overlay / injection graphique** : abandonné puis retiré à la demande du joueur.
- **Pas de signature logicielle payante**, pas de modification des protections Windows.

---

## 7. Préférences du joueur

- Diagnostiquer **par l'analyse** (code, désassemblage, sondes en lecture seule), pas
  en multipliant les essais à l'aveugle côté joueur. Voir
  [`docs/METHODE-DIAGNOSTIC.md`](docs/METHODE-DIAGNOSTIC.md).
- Préserver les sauvegardes / checkpoints ; une action à la fois en test.
- Pas de terminal qui reste ouvert (d'où `Lancer.vbs`).
