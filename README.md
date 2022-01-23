# WatchSass.Tool
This tool watches sass/scss files for modifcations and compiles to css. 
- Will not compile partial/module files, file starting with ```'_'``` in the filename (_module.sass, _module_scss).\
- Can be configured to re-target the output file(s) to different folder(s)/file(s)



## Install
```dotnet tool install -g watchsass.tool --prelease```

## Run
By defaults monitors current working directory.
```
dotnet watch-sass watch [directory]
```
or
```
dotnet-watch-sass watch [directory]
```

### Help
```
dotnet watch-sass --help
```

## Config file
Config file is used to configure the output target css file from compiled sass/scss\
Target can be either folder or file. If the target is a folder, then the css file will be named the same as the sass file.

Config file ```sass-watcher-tool.json``` is searched for in the root of the path being monitored.

### Config file layout
```json
{  
   "sources": [
      {
         "source": "src/styles/main.sass",
         "target": "src/dist/css/main.css"
      }
   ] 
}
```