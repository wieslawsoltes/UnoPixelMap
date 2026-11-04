// Real WebGL2 fixture for the production Uno framebuffer adapter.
#include <GLES3/gl3.h>
#include <emscripten.h>
#include <emscripten/html5_webgl.h>
#include <stdint.h>
#include <stdlib.h>
#include <string.h>

int __wrap_uno_gl_resolve(const char *name);

int uno_gl_resolve(const char *name)
{
    return strcmp(name, "glReadPixels") == 0 ? (int)(intptr_t)&glReadPixels : 0;
}

EMSCRIPTEN_KEEPALIVE
int proof_context(int index)
{
    EmscriptenWebGLContextAttributes attributes;
    emscripten_webgl_init_context_attributes(&attributes);
    attributes.majorVersion = 2;
    attributes.minorVersion = 0;
    attributes.antialias = EM_FALSE;
    attributes.alpha = EM_TRUE;
    EMSCRIPTEN_WEBGL_CONTEXT_HANDLE context = emscripten_webgl_create_context(index == 0 ? "#one" : "#two", &attributes);
    if (!context || emscripten_webgl_make_context_current(context) != EMSCRIPTEN_RESULT_SUCCESS) return 0;
    return context;
}

EMSCRIPTEN_KEEPALIVE
int proof_read(int width, int height, void *pixels)
{
    typedef void (*ReadPixels)(GLint, GLint, GLsizei, GLsizei, GLenum, GLenum, void *);
    ReadPixels read_pixels = (ReadPixels)(intptr_t)__wrap_uno_gl_resolve("glReadPixels");
    read_pixels(0, 0, width, height, 0x80E1, GL_UNSIGNED_BYTE, pixels);
    return glGetError();
}

EMSCRIPTEN_KEEPALIVE
void *proof_alloc(int bytes) { return malloc((size_t)bytes); }

EMSCRIPTEN_KEEPALIVE
void proof_free(void *pointer) { free(pointer); }
