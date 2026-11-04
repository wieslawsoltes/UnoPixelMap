// Uno 6.7 resolves GLCanvasElement entry points through native Emscripten GL.
// Its browser readback still requests BGRA, which only Uno's managed/JS fallback
// translates. Wrap that resolver at link time so the native route performs the
// same RGBA read and channel conversion. All other entry points retain Uno's
// implementation, including its sized-texture-format adapters.

#include <GLES3/gl3.h>
#include <emscripten.h>
#include <stdint.h>
#include <stddef.h>
#include <string.h>

#define PIXEL_MAP_BGRA 0x80E1

extern int __real_uno_gl_resolve(const char *name);

static void pixel_map_read_pixels(GLint x, GLint y, GLsizei width, GLsizei height,
                                  GLenum format, GLenum type, void *pixels)
{
    if (format != PIXEL_MAP_BGRA || type != GL_UNSIGNED_BYTE || pixels == NULL || width <= 0 || height <= 0)
    {
        glReadPixels(x, y, width, height, format, type, pixels);
        return;
    }

    GLint pack_buffer = 0;
    glGetIntegerv(GL_PIXEL_PACK_BUFFER_BINDING, &pack_buffer);
    if (pack_buffer != 0)
    {
        // A PBO argument is a buffer offset. Native validation handles unsupported
        // BGRA PBO requests and preserves their existing GL error behavior.
        glReadPixels(x, y, width, height, format, type, pixels);
        return;
    }

    GLint row_length = 0, skip_rows = 0, skip_pixels = 0, alignment = 4;
    glGetIntegerv(GL_PACK_ROW_LENGTH, &row_length);
    glGetIntegerv(GL_PACK_SKIP_ROWS, &skip_rows);
    glGetIntegerv(GL_PACK_SKIP_PIXELS, &skip_pixels);
    glGetIntegerv(GL_PACK_ALIGNMENT, &alignment);

    glReadPixels(x, y, width, height, GL_RGBA, type, pixels);

    // Keep GL's error flag available to GLCanvasElement's readback validation.
    // As with glReadPixels, the caller owns storage for the selected pack layout.
    size_t row_bytes = (size_t)(row_length > 0 ? row_length : width) * 4;
    size_t pack_alignment = alignment > 0 ? (size_t)alignment : 4;
    size_t stride = (row_bytes + pack_alignment - 1) & ~(pack_alignment - 1);
    GLsizei columns_before_last_row = stride < (size_t)width * 4 ? (GLsizei)(stride / 4) : width;
    uint8_t *first_row = (uint8_t *)pixels + (size_t)skip_rows * stride + (size_t)skip_pixels * 4;
    for (GLsizei row = 0; row < height; row++)
    {
        uint8_t *pixel = first_row + (size_t)row * stride;
        // Overlapping packed rows share storage. Convert each written pixel once.
        GLsizei columns = row + 1 < height ? columns_before_last_row : width;
        for (GLsizei column = 0; column < columns; column++, pixel += 4)
        {
            uint8_t red = pixel[0];
            pixel[0] = pixel[2];
            pixel[2] = red;
        }
    }
}

EMSCRIPTEN_KEEPALIVE
int __wrap_uno_gl_resolve(const char *name)
{
    if (name != NULL && strcmp(name, "glReadPixels") == 0)
        return (int)(intptr_t)&pixel_map_read_pixels;
    return __real_uno_gl_resolve(name);
}
