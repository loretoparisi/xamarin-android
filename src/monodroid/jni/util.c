#include <assert.h>
#include <stdlib.h>
#include <stdarg.h>
#include <stdio.h>
#include <errno.h>
#ifndef WINDOWS
#include <sys/socket.h>
#else
#include <Winsock2.h>
#endif
#include <sys/stat.h>
#include <sys/types.h>
#include <string.h>

#ifdef WINDOWS
#include <direct.h>
#include <stringapiset.h>
#endif

#include "monodroid.h"
#include "dylib-mono.h"
#include "util.h"
#include "logger.h"
#include "monodroid-glue.h"

int
ends_with (const char *str, const char *end)
{
	char *p;

	p = strstr (str, end);

	return p != NULL && p [strlen (end)] == 0;
}

char*
path_combine(const char *path1, const char *path2)
{
	// Don't let erroneous NULL parameters situation propagate
	assert (path1 != NULL || path2 != NULL);

	if (path1 == NULL)
		return strdup (path2);
	if (path2 == NULL)
		return strdup (path1);
	return monodroid_strdup_printf ("%s"MONODROID_PATH_SEPARATOR"%s", path1, path2);
}

static char package_property_suffix [9];

static void
add_to_vector (char ***vector, int size, char *token)
{
	*vector = *vector == NULL ? 
		(char **)xmalloc(2 * sizeof(*vector)) :
		(char **)xrealloc(*vector, (size + 1) * sizeof(*vector));
		
	(*vector)[size - 1] = token;
}

void
monodroid_strfreev (char **str_array)
{
	char **orig = str_array;
	if (str_array == NULL)
		return;
	while (*str_array != NULL){
		free (*str_array);
		str_array++;
	}
	free (orig);
}

char ** 
monodroid_strsplit (const char *string, const char *delimiter, int max_tokens)
{
	const char *c;
	char *token, **vector;
	int size = 1;
	
	if (strncmp (string, delimiter, strlen (delimiter)) == 0) {
		vector = (char **)xmalloc (2 * sizeof(vector));
		vector[0] = strdup ("");
		size++;
		string += strlen (delimiter);
	} else {
		vector = NULL;
	}

	while (*string && !(max_tokens > 0 && size >= max_tokens)) {
		c = string;
		if (strncmp (string, delimiter, strlen (delimiter)) == 0) {
			token = strdup ("");
			string += strlen (delimiter);
		} else {
			while (*string && strncmp (string, delimiter, strlen (delimiter)) != 0) {
				string++;
			}

			if (*string) {
				int toklen = (string - c);
				token = xmalloc (toklen + 1);
				strncpy (token, c, toklen);
				token [toklen] = '\0';

				/* Need to leave a trailing empty
				 * token if the delimiter is the last
				 * part of the string
				 */
				if (strcmp (string, delimiter) != 0) {
					string += strlen (delimiter);
				}
			} else {
				token = strdup (c);
			}
		}
			
		add_to_vector (&vector, size, token);
		size++;
	}

	if (*string) {
		if (strcmp (string, delimiter) == 0)
			add_to_vector (&vector, size, strdup (""));
		else {
			/* Add the rest of the string as the last element */
			add_to_vector (&vector, size, strdup (string));
		}
		size++;
	}
	
	if (vector == NULL) {
		vector = (char **) xmalloc (2 * sizeof (vector));
		vector [0] = NULL;
	} else if (size > 0) {
		vector[size - 1] = NULL;
	}
	
	return vector;
}

char *
monodroid_strdup_printf (const char *format, ...)
{
        char *ret;
        va_list args;
        int n;

        va_start (args, format);
        n = vasprintf (&ret, format, args);
        va_end (args);
        if (n == -1)
                return NULL;

        return ret;
}

int
send_uninterrupted (int fd, void *buf, int len)
{
	int res;
	
	do {
		res = send (fd, buf, len, 0);
	} while (res == -1 && errno == EINTR);

	return res == len;
}

int
recv_uninterrupted (int fd, void *buf, int len)
{
	int res;
	int total = 0;
	int flags = 0;

	do { 
		res = recv (fd, (char *) buf + total, len - total, flags); 
		if (res > 0)
			total += res;
	} while ((res > 0 && total < len) || (res == -1 && errno == EINTR));

	return total;
}

void
monodroid_store_package_name (const char *name)
{
	const char *ch;
	int hash;

	memset (package_property_suffix, 0, sizeof (package_property_suffix));
	if (!name || strlen (name) == 0)
		return;

	/* Android properties can be at most 32 bytes long (!) and so we mustn't append the package name
	 * as-is since it will most likely generate conflicts (packages tend to be named
	 * com.mycompany.app), so we simply generate a hash code and use that instead. We treat the name
	 * as a stream of bytes assumming it's an ASCII string using a simplified version of the hash
	 * algorithm used by BCL's String.GetHashCode ()
	 */
	ch = name;
	hash = 0;
	while (*ch)
		hash = (hash << 5) - (hash + *ch++);
	snprintf (package_property_suffix, sizeof (package_property_suffix), "%08x", hash);
	log_info (LOG_DEFAULT, "Generated hash 0x%s for package name %s", package_property_suffix, name);
}

int
monodroid_get_namespaced_system_property (const char *name, char **value)
{
	char *local_value = NULL;
	int result = -1;

	if (value)
		*value = NULL;

	if (strlen (package_property_suffix) > 0) {
		log_info (LOG_DEFAULT, "Trying to get property %s.%s", name, package_property_suffix);
		char *propname = monodroid_strdup_printf ("%s.%s", name, package_property_suffix);
		if (propname) {
			result = monodroid_get_system_property (propname, &local_value);
			free (propname);
		}
	}

	if (result <= 0 || !local_value)
		result = monodroid_get_system_property (name, &local_value);

	if (result > 0) {
		if (strlen (local_value) == 0) {
			free (local_value);
			return 0;
		}

		log_info (LOG_DEFAULT, "Property '%s' has value '%s'.", name, local_value);

		if (value)
			*value = local_value;
		else
			free (local_value);
		return result;
	}

	return monodroid_get_system_property_from_overrides (name, value);
}

MonoAssembly *
monodroid_load_assembly (struct DylibMono *mono, MonoDomain *domain, const char *basename)
{
	MonoAssemblyName     *assm;
	MonoAssemblyName     *aname;
	MonoImageOpenStatus   status;

	aname = mono->mono_assembly_name_new (basename);
	MonoDomain *current = mono->mono_domain_get ();

	if (domain != current) {
		mono->mono_domain_set (domain, FALSE);
		assm  = mono->mono_assembly_load_full (aname, NULL, &status, 0);
		mono->mono_domain_set (current, FALSE);
	} else {
		assm  = mono->mono_assembly_load_full (aname, NULL, &status, 0);
	}

	mono->mono_assembly_name_free (aname);

	if (!assm) {
		log_fatal (LOG_DEFAULT, "Unable to find assembly '%s'.", basename);
		exit (FATAL_EXIT_MISSING_ASSEMBLY);
	}
	return assm;
}

void *
monodroid_runtime_invoke (struct DylibMono *mono, MonoDomain *domain, MonoMethod *method, void *obj, void **params, MonoObject **exc)
{
	MonoDomain *current = mono->mono_domain_get ();
	if (domain != current) {
		mono->mono_domain_set (domain, FALSE);
		void *r = mono->mono_runtime_invoke (method, obj, params, exc);
		mono->mono_domain_set (current, FALSE);
		return r;
	} else {
		return mono->mono_runtime_invoke (method, obj, params, exc);
	}
}

static int
make_directory (const char *path, int mode)
{
#if WINDOWS
	return mkdir (path);
#else
	return mkdir (path, mode);
#endif
}

int
create_directory (const char *pathname, int mode)
{
	if (mode <= 0)
		mode = DEFAULT_DIRECTORY_MODE;

	if  (!pathname || *pathname == '\0') {
		errno = EINVAL;
		return -1;
	}

	mode_t oldumask = umask (022);
	char *path = strdup (pathname);
	int rv, ret = 0;
	for (char *d = path; *d; ++d) {
		if (*d != '/')
			continue;
		*d = 0;
		if (*path) {
			rv = make_directory (path, mode);
			if  (rv == -1 && errno != EEXIST)  {
				ret = -1;
				break;
			}
		}
		*d = '/';
	}
	free (path);
	if (ret == 0)
		ret = make_directory (pathname, mode);
	umask (oldumask);

	return ret;
}

void create_public_directory (const char *dir)
{
#ifndef WINDOWS
	mode_t m = umask (0);
	mkdir (dir, 0777);
	umask (m);
#else
	wchar_t *buffer = utf8_to_utf16 (dir);
	_wmkdir (buffer);
	free (buffer);
#endif
}

FILE *
monodroid_fopen (const char *filename, const char *mode)
{
#ifndef WINDOWS
	/* On Unix, both path and system calls are all assumed
	 * to be UTF-8 compliant.
	 */
	return fopen (filename, mode);
#else
	// Convert the path and mode to a UTF-16 and then use the wide variant of fopen
	wchar_t *wpath = utf8_to_utf16 (filename);
	wchar_t *wmode = utf8_to_utf16 (mode);

	FILE* file = _wfopen (wpath, wmode);
	free (wpath);
	free (wmode);

	return file;
#endif // ndef WINDOWS
}

#ifdef WINDOWS
char*
utf16_to_utf8 (const wchar_t *widestr)
{
	int required_size = WideCharToMultiByte (CP_UTF8, 0, widestr, -1, NULL, 0, NULL, NULL);
	char *mbstr = calloc (required_size, sizeof (char));
	int converted_size = WideCharToMultiByte (CP_UTF8, 0, widestr, -1, mbstr, required_size, NULL, NULL);

	assert (converted_size == required_size);

	return mbstr;
}

wchar_t*
utf8_to_utf16 (const char *mbstr)
{
	int required_chars = MultiByteToWideChar (CP_UTF8, 0, mbstr, -1, NULL, 0);
	wchar_t *widestr = calloc (required_chars, sizeof (wchar_t));
	int converted_chars = MultiByteToWideChar (CP_UTF8, 0, mbstr, -1, widestr, required_chars);

	assert (converted_chars == required_chars);

	return widestr;
}
#endif // def WINDOWS
