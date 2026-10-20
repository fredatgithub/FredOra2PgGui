# Fred Ora2Pg Gui application

It is based on darold's ora2pg free tool.
[ora2pg web site](https://github.com/darold/ora2pg)

It is a free tool used to migrate an Oracle database to a PostgreSQL compatible schema. It connects your Oracle database, scan it automatically and extracts its structure or data, it then generates SQL scripts that you can load into PostgreSQL.

## Première interface WPF

L'application cible `net9.0-windows`. L'onglet **Connexions** permet de saisir et tester une connexion Oracle (hôte, port, nom du service) et une connexion PostgreSQL. **Enregistrer** conserve les paramètres ; ils sont également sauvegardés à la fermeture.

Les boutons d'action sont bleus au démarrage et pendant l'opération, verts après une réussite et rouges après un échec. Cela s'applique aux tests de connexion, à l'enregistrement et au chargement des objets.

Les hôtes, noms des bases/services, noms d'utilisateur et mots de passe du fichier de paramètres sont protégés par DPAPI, avec la portée du compte Windows courant. Le fichier `settings.json` et le journal `fredora2pg.log` sont placés dans le répertoire de l'application, à côté de l'exécutable. Ce répertoire doit être accessible en écriture par l'utilisateur. Si un ancien `settings.json` existe dans `%LOCALAPPDATA%\FredOra2PgGui`, il est repris au premier démarrage puis supprimé de son ancien emplacement. Les dimensions et la position de la fenêtre sont enregistrées dans le même fichier JSON.

Dans l'onglet **Connexions**, le champ **Propriétaire (owner)** limite l'inventaire Oracle à ce propriétaire et le champ **Schéma** limite l'inventaire PostgreSQL à ce schéma. Ils sont facultatifs : s'ils restent vides, l'inventaire inclut tous les propriétaires/schémas non système.

Le bouton **Charger les objets** affiche une fenêtre d'attente pendant qu'il interroge les deux bases et remplit les onglets Tables, Vues, Déclencheurs, Procédures stockées et Séquences. Les noms source et cible sont affichés dans deux colonnes indépendantes ; cet inventaire ne réalise pas encore de comparaison ni de migration.

L'onglet **Scripts**, placé avant **Log**, permet de lancer ora2pg pour générer des scripts SQL. Il faut indiquer l'exécutable ora2pg (ou son nom s'il est accessible dans le `PATH`), un fichier de configuration ora2pg valide, le dossier de sortie et le type d'export. **Create Scripts** lance ora2pg avec ces paramètres ; la grille liste les fichiers `.sql` présents dans le dossier de sortie avec leur nom, type, emplacement, taille et date de modification. Les paramètres de cet onglet sont conservés avec les autres paramètres de l'application.
