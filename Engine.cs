using System.Text.Json;
using Silk.NET.Maths;
using Silk.NET.SDL;
using TheAdventure.Models;
using TheAdventure.Models.Data;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace TheAdventure
{
    public class Engine
    {
        private readonly Dictionary<int, GameObject> _gameObjects = new();
        private readonly Dictionary<string, TileSet> _loadedTileSets = new();

        private Level? _currentLevel;
        private PlayerObject? _player;
        private GameRenderer _renderer;
        private Input _input;

        private DateTimeOffset _lastUpdate = DateTimeOffset.Now;

        public Engine(GameRenderer renderer, Input input)
        {
            _renderer = renderer;
            _input = input;
            _input.OnMouseClick += (_, coords) => AddBomb(coords.x, coords.y);
        }

        public void InitializeWorld()
        {
            var jsonSerializerOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var levelContent = File.ReadAllText(Path.Combine("Assets", "terrain.tmj"));
            var level = JsonSerializer.Deserialize<Level>(levelContent, jsonSerializerOptions);
            if (level == null) return;

            foreach (var refTileSet in level.TileSets)
            {
                if (string.IsNullOrEmpty(refTileSet.Source))
                {
                    Console.WriteLine("TileSet source is null or empty.");
                    continue; // Skip to the next iteration of the loop
                }

                if (!_loadedTileSets.TryGetValue(refTileSet.Source, out var tileSet))
                {
                    var tileSetContent = File.ReadAllText(Path.Combine("Assets", refTileSet.Source));
                    tileSet = JsonSerializer.Deserialize<TileSet>(tileSetContent, jsonSerializerOptions);

                    if (tileSet == null)
                    {
                        Console.WriteLine($"Failed to deserialize TileSet from source {refTileSet.Source}");
                        continue; // Skip to the next iteration of the loop
                    }

                    foreach (var tile in tileSet.Tiles)
                    {
                        if (string.IsNullOrEmpty(tile.Image))
                        {
                            Console.WriteLine("Tile image path is null or empty.");
                            continue; // Skip to the next tile
                        }

                        var internalTextureId = _renderer.LoadTexture(Path.Combine("Assets", tile.Image), out _);
                        tile.InternalTextureId = internalTextureId;
                    }
                    _loadedTileSets[refTileSet.Source] = tileSet;
                }

                refTileSet.Set = tileSet; // It's safe to assign tileSet to refTileSet.Set here because we've handled the possibility of null
            }



            _currentLevel = level;
            var spriteSheet = SpriteSheet.LoadSpriteSheet("player.json", "Assets", _renderer);
            if (spriteSheet != null)
            {
                _player = new PlayerObject(spriteSheet, 100, 100);
            }
            _renderer.SetWorldBounds(new Rectangle<int>(0, 0, _currentLevel.Width * _currentLevel.TileWidth,
                _currentLevel.Height * _currentLevel.TileHeight));
        }

        public void ProcessFrame()
        {
            var currentTime = DateTimeOffset.Now;
            var secsSinceLastFrame = (currentTime - _lastUpdate).TotalSeconds;
            _lastUpdate = currentTime;

            bool up = _input.IsUpPressed();
            bool down = _input.IsDownPressed();
            bool left = _input.IsLeftPressed();
            bool right = _input.IsRightPressed();
            bool isAttacking = _input.IsKeyAPressed();
            bool isSprinting = _input.IsSprintPressed();
            bool addBomb = _input.IsKeyBPressed();

            if (isAttacking)
            {
                var dir = (up ? 1 : 0) + (down ? 1 : 0) + (left ? 1 : 0) + (right ? 1 : 0);
                if (dir <= 1)
                {
                    _player?.Attack(up, down, left, right);
                }
                else
                {
                    isAttacking = false;
                }
            }

            if (!isAttacking)
            {
                // Check if both _player and _currentLevel are not null before attempting to update player position
                if (_player != null && _currentLevel != null)
                {
                    _player.UpdatePlayerPosition(
                        up ? 1.0 : 0.0,
                        down ? 1.0 : 0.0,
                        left ? 1.0 : 0.0,
                        right ? 1.0 : 0.0,
                        _currentLevel.Width * _currentLevel.TileWidth,
                        _currentLevel.Height * _currentLevel.TileHeight,
                        max(secsSinceLastFrame, 0.01), // make movement work for higher refresh rates rip 240hz
                        isSprinting
                    );
                }
                else
                {
                    Console.WriteLine("Player or level data is not available.");
                }
            }


            // Declare itemsToRemove here to cover all subsequent usage in this method
            var itemsToRemove = GetAllTemporaryGameObjects()
                .Where(gameObject => gameObject.IsExpired)
                .Select(gameObject => gameObject.Id)
                .ToList();

            if (addBomb && _player != null)
            {
                AddBomb(_player.Position.X, _player.Position.Y, false);
            }

            foreach (var gameObjectId in itemsToRemove)
            {
                if (_gameObjects.TryGetValue(gameObjectId, out var gameObject) && gameObject is TemporaryGameObject tempObject && _player != null)
                {
                    var deltaX = Math.Abs(_player.Position.X - tempObject.Position.X);
                    var deltaY = Math.Abs(_player.Position.Y - tempObject.Position.Y);
                    if (deltaX < 32 && deltaY < 32)
                    {
                        _player.GameOver();
                    }
                }
                _gameObjects.Remove(gameObjectId);
            }
        }

        private double max(double a, double b)
        {
            return a < b ? b : a;
        }

        public void RenderFrame()
        {
            _renderer.SetDrawColor(0, 0, 0, 255);
            _renderer.ClearScreen();

            // Check if _player is not null before accessing its properties
            if (_player != null)
            {
                _renderer.CameraLookAt(_player.Position.X, _player.Position.Y);
            }
            else
            {
                _renderer.CameraLookAt(0, 0); // Default position or handle appropriately
                Console.WriteLine("Warning: Player object is null when rendering frame.");
            }

            RenderTerrain();
            RenderAllObjects();
            _renderer.PresentFrame();
        }


        private void RenderTerrain()
        {
            if (_currentLevel == null) return;
            for (int layer = 0; layer < _currentLevel.Layers.Length; ++layer)
            {
                var cLayer = _currentLevel.Layers[layer];
                for (int i = 0; i < _currentLevel.Width; ++i)
                {
                    for (int j = 0; j < _currentLevel.Height; ++j)
                    {
                        var cTileId = cLayer.Data[j * cLayer.Width + i] - 1;
                        var cTile = GetTile(cTileId);
                        if (cTile == null) continue;
                        var src = new Rectangle<int>(0, 0, cTile.ImageWidth, cTile.ImageHeight);
                        var dst = new Rectangle<int>(i * cTile.ImageWidth, j * cTile.ImageHeight, cTile.ImageWidth, cTile.ImageHeight);
                        _renderer.RenderTexture(cTile.InternalTextureId, src, dst);
                    }
                }
            }
        }

        private IEnumerable<TemporaryGameObject> GetAllTemporaryGameObjects()
        {
            return _gameObjects.Values.OfType<TemporaryGameObject>();
        }

        private void RenderAllObjects()
        {
            foreach (var gameObject in _gameObjects.Values.OfType<RenderableGameObject>())
            {
                gameObject.Render(_renderer);
            }

            // Check if _player is not null before attempting to render it
            if (_player != null)
            {
                _player.Render(_renderer);
            }
            else
            {
                // Optionally log or handle the situation where _player is null
                Console.WriteLine("Warning: Attempted to render a null player object.");
            }
        }


        private void AddBomb(int x, int y, bool translateCoordinates = true)
        {
            var translated = translateCoordinates ? _renderer.TranslateFromScreenToWorldCoordinates(x, y) : new Vector2D<int>(x, y);
            var spriteSheet = SpriteSheet.LoadSpriteSheet("bomb.json", "Assets", _renderer);
            if (spriteSheet != null)
            {
                spriteSheet.ActivateAnimation("Explode");
                TemporaryGameObject bomb = new(spriteSheet, 2.1, (translated.X, translated.Y));
                _gameObjects.Add(bomb.Id, bomb);
            }
        }

        private Tile GetTile(int id)
        {
            if (_currentLevel == null) throw new InvalidOperationException("Current level is not loaded.");
            foreach (var tileSet in _currentLevel.TileSets)
            {
                foreach (var tile in tileSet.Set.Tiles)
                {
                    if (tile.Id == id)
                    {
                        return tile;
                    }
                }
            }
            throw new KeyNotFoundException($"No tile with ID {id} found.");
        }
    }
}
