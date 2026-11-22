#include <arpa/inet.h>
#include <errno.h>
#include <linux/errqueue.h>
#include <netinet/in.h>
#include <poll.h>
#include <stdint.h>
#include <string.h>
#include <sys/socket.h>
#include <unistd.h>

int emi_hop(const uint8_t *address, int size, int ttl, char *peer, int capacity) {
    int v6 = size == 16;
    int family = v6 ? AF_INET6 : AF_INET;
    int level = v6 ? IPPROTO_IPV6 : IPPROTO_IP;
    int errors = v6 ? IPV6_RECVERR : IP_RECVERR;
    int fd = socket(family, SOCK_DGRAM, 0), one = 1, result = -1;
    if (fd < 0) return -1;
    if (setsockopt(fd, level, errors, &one, sizeof(one)) ||
        setsockopt(fd, level, v6 ? IPV6_UNICAST_HOPS : IP_TTL, &ttl, sizeof(ttl))) goto end;
    struct sockaddr_storage target = {0};
    socklen_t length;
    if (v6) {
        struct sockaddr_in6 *d = (void *)&target;
        d->sin6_family = family; d->sin6_port = htons(33434 + ttl);
        memcpy(&d->sin6_addr, address, 16); length = sizeof(*d);
    } else {
        struct sockaddr_in *d = (void *)&target;
        d->sin_family = family; d->sin_port = htons(33434 + ttl);
        memcpy(&d->sin_addr, address, 4); length = sizeof(*d);
    }
    if (sendto(fd, "E", 1, 0, (struct sockaddr *)&target, length) < 0) goto end;
    result = 0;
    struct pollfd p = {fd, POLLERR, 0};
    if (poll(&p, 1, 1000) > 0) {
        union { struct cmsghdr alignment; char bytes[512]; } control;
        char buf[32]; struct iovec vec = {buf, sizeof(buf)};
        struct msghdr msg = {0};
        msg.msg_iov = &vec; msg.msg_iovlen = 1; msg.msg_control = control.bytes; msg.msg_controllen = sizeof(control);
        if (recvmsg(fd, &msg, MSG_ERRQUEUE) >= 0) {
            for (struct cmsghdr *c = CMSG_FIRSTHDR(&msg); c; c = CMSG_NXTHDR(&msg,c)) {
                if (c->cmsg_level != level || c->cmsg_type != errors) continue;
                struct sockaddr *sa = SO_EE_OFFENDER((struct sock_extended_err *)CMSG_DATA(c));
                if (sa->sa_family != family) continue;
                void *a = v6 ? (void *)&((struct sockaddr_in6 *)sa)->sin6_addr : (void *)&((struct sockaddr_in *)sa)->sin_addr;
                inet_ntop(family, a, peer, capacity); result = !memcmp(a, address, size);
            }
        }
    }
end:
    close(fd);
    return result;
}
