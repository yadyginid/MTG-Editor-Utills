#include <stdio.h>
#include "lua.h"
#include "lauxlib.h"

static int write_chunk(lua_State *state, const void *data, size_t size, void *user_data)
{
  FILE *output = (FILE *)user_data;
  (void)state;
  return fwrite(data, 1, size, output) == size ? 0 : 1;
}

int main(int argc, char **argv)
{
  lua_State *state;
  FILE *output;
  int status;

  if (argc != 3) {
    fprintf(stderr, "usage: mdluac input.lua output.luac\n");
    return 2;
  }

  state = luaL_newstate();
  if (state == NULL) {
    fprintf(stderr, "unable to create Lua state\n");
    return 3;
  }

  status = luaL_loadfile(state, argv[1]);
  if (status != 0) {
    fprintf(stderr, "%s\n", lua_tostring(state, -1));
    lua_close(state);
    return 4;
  }

  output = fopen(argv[2], "wb");
  if (output == NULL) {
    fprintf(stderr, "unable to open output file\n");
    lua_close(state);
    return 5;
  }

  /* Magic Duels needs the source/debug metadata emitted by lua_dump() in
     standalone custom card chunks. Fully stripped chunks are valid Lua 5.1,
     but the match loader silently ignores them. */
  status = lua_dump(state, write_chunk, output);
  if (fclose(output) != 0 && status == 0)
    status = 1;
  if (status == 0) {
    output = fopen(argv[2], "r+b");
    if (output == NULL || fseek(output, 5, SEEK_SET) != 0 || fputc(0x5F, output) == EOF)
      status = 1;
    if (output != NULL && fclose(output) != 0)
      status = 1;
  }
  lua_close(state);
  return status == 0 ? 0 : 6;
}
