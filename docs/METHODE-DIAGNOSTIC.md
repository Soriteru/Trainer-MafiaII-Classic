# Méthode de diagnostic des bugs et des crashes

Ce document explique **comment on trouve la cause d'un bug ou d'un crash sur ce
projet, et comment on le corrige**. C'est la partie la plus importante pour reprendre
le travail : la plupart des problèmes rencontrés sont des **crashes du jeu**, souvent
sans boîte d'erreur, et la bonne démarche fait gagner énormément de temps.

Principe de base : **diagnostiquer par l'analyse, pas par tâtonnement.** On trouve la
cause par le code, le désassemblage et des sondes en lecture seule, puis on corrige en
visant juste. On ne fait pas multiplier à l'utilisateur des essais A/B à l'aveugle ;
on lui demande seulement le **contexte du crash** et une **confirmation finale** en
jeu une fois le correctif prêt.

Si tu as une meilleure méthode, utilise-la — ce qui suit est ce qui a fonctionné
jusqu'ici, pas un dogme.

---

## 1. Recueillir le contexte exact (sans faire tester)

Un crash n'a de sens qu'avec son contexte. Demande / note :

- **Où** : chapitre, mission, lieu (free-roam, cinématique, ascenseur, customisation…).
- **Quoi** : l'action au moment précis du crash (tirer, ramasser une arme, entrer dans
  un véhicule, sortir d'une cinématique…).
- **Quels cheats étaient actifs** (la combinaison compte).
- **Le symptôme** : fermeture instantanée sans boîte ? boîte « Runtime » C++ (ex.
  R6025) ? gel ? Chaque forme pointe vers une famille de causes différente.
- Un éventuel `trainer.log` et un dump Windows s'il y en a un.

## 2. Isoler : le trainer est-il en cause ?

Le test d'isolation fondamental, que le **joueur** peut faire une seule fois :

- **Trainer fermé, même situation → pas de crash** ⟹ le trainer (nos hooks / écritures)
  est impliqué.
- **Trainer fermé → crash quand même** ⟹ c'est le jeu / sa configuration (PhysX,
  pilotes, mémoire), pas notre code.

Ensuite, pour resserrer **sans** série d'essais pénible : on raisonne sur **quelle
fonction** peut toucher le code concerné au moment du crash, à partir du contexte et
de la lecture du code. On ne demande au joueur de réessayer qu'une fois, pour
**confirmer** l'hypothèse déjà construite par l'analyse.

## 3. Construire la cause par l'analyse

Les outils, du moins au plus lourd :

1. **Lecture du code** — relire le hook / l'écriture suspect. Beaucoup de causes sont
   visibles : un pointeur déréférencé sans vérification, un cache non réinitialisé, un
   drapeau mal interprété.
2. **Désassemblage** (Capstone) — pour les *code-caves* x86 générés, vérifier octet par
   octet ce que la routine fait réellement en mémoire. Les binaires exportés
   (`--export-hooks`, `--export-experimental`) servent de référence.
3. **Sondes en lecture seule** — de petits scripts qui *lisent* la mémoire du jeu en
   direct (jamais d'écriture) pour observer un état : un drapeau de contrôle, une
   structure joueur, une valeur avant/pendant/après une transition. C'est ainsi qu'on
   a trouvé le signal « pas en contrôle » (voir §5).
4. **Émulation** (Unicorn) — exécuter une routine x86 isolée pour vérifier son
   comportement hors du jeu.

On formule une **hypothèse précise** (« tel pointeur est nul dans tel cas »), puis on
la confronte au code / à la sonde avant de toucher quoi que ce soit.

## 4. Corriger en visant juste, avec un garde-fou

Le correctif doit traiter **la cause**, pas le symptôme, et rester le plus localisé
possible. On préfère :

- **Vérifier avant de déréférencer** (null + plage d'adresse) plutôt que supprimer la
  fonction.
- **Retomber sur le comportement normal du jeu** quand une condition n'est pas sûre
  (ne rien écrire) plutôt que forcer.
- **Réversibilité** : les routines d'origine sont restaurées au retrait d'un hook.

On ajoute, quand c'est possible, un **test de régression** dans `--self-test` qui
reproduit la condition fautive hors jeu.

## 5. Études de cas réelles

Ces cas illustrent la démarche. Les détails complets (hash, sauvegardes) sont dans le
journal de suivi.

### a) Crash au ramassage d'un fusil à pompe (hook munitions)

- **Contexte** : fusillade intense + ramassage d'un fusil à pompe. Crash seulement avec
  « Chargeur illimité / Munitions » actifs → coupable = le hook munitions `0x5A46E0`.
- **Analyse** (lecture du cave + désassemblage) : le cave faisait
  `mov edx,[ecx+8]; cmp [edx+4],1000` **sans vérifier `edx`**, alors que `[ecx+4]`
  juste avant était testé. Au ramassage du pompe, le jeu appelle la routine avec un
  `ecx` dont `[ecx+8]` est nul/invalide → déréférence de `[edx+4]` → access violation.
- **Correctif** : test null + plage sur **les deux** pointeurs avant déréférencement ;
  sinon comportement normal du jeu. Cave revérifié au désassemblage. Test de régression
  ajouté.
- **Leçon** : un crash « aléatoire » en combat était en fait un pointeur non gardé sur
  un chemin de code rare (ramassage d'arme). Trouvé par lecture, pas par essais.

### b) Cheats coupés en fusillade (faux positif)

- **Symptôme** : après l'ajout d'une suspension automatique, **tous les cheats se
  coupaient en fusillade**.
- **Analyse** (sonde lecture seule pendant le combat) : `joueur+0x22` bit `0x20` = 0
  (bien en contrôle) mais `jeu+0x131` bit 0 = 0 **aussi** → la condition « en contrôle »
  renvoyait faux à tort. Conclusion : `jeu+0x131` bit 0 **n'est pas** un drapeau de
  contrôle ; sa correspondance observée pendant une cinématique était une coïncidence.
- **Correctif** : ne garder que `joueur+0x22` bit `0x20` comme signal de contrôle
  (validé 0 en jeu normal ET en fusillade, 1 en cinématique).
- **Leçon** : valider un signal mémoire sur **plusieurs** états (normal, combat,
  cinématique) avant de s'y fier. Une seule observation peut être une coïncidence.

### c) Hooks réinstallés mais inertes après une cinématique

- **Symptôme** : après une cinématique, un interrupteur reste « allumé » mais l'effet
  est mort en jeu.
- **Analyse** (lecture du code) : `Dispose()` remettait à zéro les drapeaux du cave
  mais **pas le cache** `lastPlayer/lastReload/...`. Au retour, `Set()` réinstallait
  les trampolines mais son optimisation « rien n'a changé » **sautait** la réécriture
  des drapeaux → hooks présents mais inertes.
- **Correctif** : `Dispose()` réinitialise aussi le cache → la réactivation réécrit
  joueur, inventaire **et** drapeaux. Test de régression ajouté.
- **Leçon** : attention aux optimisations « rien n'a changé » qui sautent une étape
  après une remise à zéro partielle.

### d) Crashes de transition (cinématiques, ascenseurs, chargements de zone)

- **Contexte** : fermetures instantanées en entrant dans un ascenseur / en sortant
  d'une cinématique, avec des cheats actifs. Isolation : trainer fermé = pas de crash
  → nos écritures tapaient dans des objets que le jeu détruit/reconstruit pendant le
  chargement de scène.
- **Analyse** (sondes + capture avant/pendant/après) : on a trouvé un drapeau fiable
  « pas en contrôle » (`joueur+0x22` bit `0x20`), stable sur de nombreuses lectures.
- **Correctif** : tant que le joueur n'est pas en contrôle (+ marge après), le trainer
  **retire tous les hooks et n'écrit rien**, puis tout reprend au retour du contrôle.
  Couvre cinématiques, ascenseurs et chargements de zone pour tous les cheats.
- **Leçon** : la bonne réponse à une famille de crashes de transition n'est pas de
  corriger chaque cas, mais de **se mettre en pause** pendant que le jeu n'est pas dans
  un état stable.

### e) Crash d'armes hors chapitre / DLC (limite du jeu, pas du trainer)

- **Contexte** : dans *Joe's Adventures*, certaines armes données par le trainer
  (M3 Grease Gun, M1A1 Thompson, M1 Garand) faisaient **planter le jeu au démarrage
  d'une fusillade** ; une autre était invisible en main ; une grenade était inerte.
- **Analyse** : les écritures per-tick sont revalidées à chaque tick, et les hooks
  per-tir sont gardés ; ces armes **tiraient sans crash avant** l'arrivée de la police
  → le tir n'est pas le déclencheur. Cause réelle : ce sont des armes de la **campagne**,
  non natives de ce DLC ; leurs **ressources ne sont pas chargées**. L'API native
  `InventoryAddWeapon` accepte l'ID, mais au démarrage d'une fusillade le jeu accède à
  des données absentes → plantage. **Limite d'assets du jeu, pas une écriture du
  trainer.** Ces armes sont normales dans leurs chapitres d'origine.
- **Correctif** : retirer du *give* seulement les cas inutilisables (grenade inerte,
  comme la MG42), **pas** de blacklist globale ; afficher un **avertissement** clair.
- **Leçon** : toutes les causes ne sont pas des bugs du trainer. Quand l'analyse montre
  une limite du jeu, on le documente et on **avertit** plutôt que de « corriger » un
  comportement qu'on ne contrôle pas.

---

## 6. Ce qu'on évite

- **Ne pas** présenter un essai hors jeu ou une valeur de retour native comme une
  validation en mission.
- **Ne pas** multiplier les essais à l'aveugle côté joueur : on isole une fois, puis on
  analyse.
- **Ne pas** appliquer aveuglément d'anciens scripts de transformation de texte
  (beaucoup ne sont pas idempotents et peuvent réintroduire une ancienne version).
- **Ne pas** adapter des offsets d'une version de `mafia2.exe` à une autre sans
  réanalyser l'exécutable. Deux hash sont connus et acceptés (original et patché 4 Go).
