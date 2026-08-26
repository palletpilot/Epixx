function showNotification(message, type = "info") {

    const notification = document.getElementById("admin-notification");
    const messageElement = document.getElementById("notification-message");

    if (!notification || !messageElement) {
        return;
    }

    messageElement.textContent = message;

    notification.classList.remove(
        "hidden",
        "notification-success",
        "notification-error",
        "notification-warning",
        "notification-info"
    );

    notification.classList.add(`notification-${type}`);

    setTimeout(() => {
        notification.classList.add("hidden");
    }, 5000);
}
function showConfirm(message, title = "Bekräfta") {
    return new Promise(resolve => {
        const modal = document.getElementById("confirm-modal");
        const titleElement = document.getElementById("confirm-title");
        const messageElement = document.getElementById("confirm-message");
        const acceptBtn = document.getElementById("confirm-accept");
        const cancelBtn = document.getElementById("confirm-cancel");

        titleElement.textContent = title;
        messageElement.textContent = message;

        modal.classList.remove("hidden");

        function close(result) {
            modal.classList.add("hidden");

            acceptBtn.removeEventListener("click", accept);
            cancelBtn.removeEventListener("click", cancel);

            resolve(result);
        }

        function accept() {
            close(true);
        }

        function cancel() {
            close(false);
        }

        acceptBtn.addEventListener("click", accept);
        cancelBtn.addEventListener("click", cancel);
    });
}