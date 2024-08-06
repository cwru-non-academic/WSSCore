# WSSInterfacingCode
C# class that has all the methods to communicate with the WSS and software specific classes that implements some of the functionalities necessary for that program.

## How to add a submodule to exiting project
1. Close other software using the code solution (Unity, Visual Studio, etc)
2. If using git desktop, open the command prompt by going to `Repository>Open in Command Prompt`
3. 1. Use command `git submodule add <submoduleURL> <pathInProject>` 
	- if using a submodule inside of Unity make sure the path is inside the Assests folder
	- Ex: `git submodule add https://github.com/cwru-non-academic/WSSInterfacingCode Assets\SubModules\WSSInterfacingModule`
4. Click `Current repository>Add>Add existing repository..` and locate the folder where you installed the submodule `<pathInProject>` 
5. Remove old scripts from the project that are now part of the submodule
6. Open solution dependent software and let it refactor.
7. (Unity Only) Make sure all you scripts in the scene are still available and linked correctly.

## How to commit a git project that has submodules (Git Desktop)
1. This only applies if the changes were made to the submodule, otherwise just commit as normal.
2. Inside of the git Desktop a warning will appear that says there are submodule chnages.
3. Changes to submodules must be commited first in the submodules repo.
4. If the submodule repo is already setup, just click open repository shortcut under the submodule changes warning.
5. If it is not setup, click the shortcut to add it to Git Desktop.
6. Once in the submodule repo, commit the changes as normal.
7. Return to the main repo and commit the changes there (The chnage in commit ID will be not be selected by default. select it and then commit). 

## How to commit a git project that has submodules (Git CMD)


## How to pull a submodule change (Git Desktop)
1. Make sure the git submodule repo is already part of your repos in git desktop
2. If not add it by following `How to add a submodule to exiting project`
3. Go to the submodule repo and pull changes as normal. 
4. Pulling a submodule will add a change in commit ID to the main repo. 
	- Multiple changes in commit ID can be commited to the main repo as a single commit and together with other changes to the main repo.
